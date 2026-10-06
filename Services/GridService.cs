using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Models.AutoDraw;

namespace autodraw_plugin.Services;

/// <summary>
/// What goes around the grid the server lays a job out on: each cell's label,
/// and - when stacking - a picture of every earlier state in its own column.
///
/// The server does all the placing. The drawing it sends is already in the
/// cells, and it takes the cells back out of whatever is submitted; nothing
/// here moves a single entity of the job. This only draws beside it.
///
/// Both kinds of thing live on layers of their own, listed with the other
/// decoration, so neither is ever submitted.
/// </summary>
public static class GridService
{
    /// <summary>Each cell's label: which item, and which substep when stacking.</summary>
    public const string LabelLayer = "AUTODRAW_GRID";

    /// <summary>The pictures of earlier states, one block reference each.</summary>
    public const string HistoryLayer = "AUTODRAW_HISTORY";

    private const string SnapshotPrefix = "ADSNAP_";

    /// <summary>The grid last drawn, for a playing job to follow.</summary>
    public static GridDTO? Last { get; private set; }

    /// <summary>Redraw the labels for the state now in the drawing.</summary>
    public static void DrawLabels(Transaction tr, Database db, BlockTableRecord btr, GridDTO? grid)
    {
        AutoDrawVisualizer.EnsureLayer(tr, db, LabelLayer);
        AutoDrawVisualizer.ClearLayer(tr, db, LabelLayer);
        Last = grid;
        if (grid?.Labels == null) return;

        // The outline of every cell the state in hand occupies, and the top of
        // the band under them: new work is taken as belonging to the cell it is
        // drawn in, and anything outside them all is refused, so where they
        // are wants to be seen rather than guessed.
        string lines = PluginSettings.Current.Gridlines;
        List<List<double>> outlined = lines == "Off" ? new List<List<double>>()
            : lines == "Live" ? grid.Cells : grid.EveryCell;
        foreach (List<double> cell in outlined ?? new List<List<double>>())
        {
            if (cell == null || cell.Count < 4) continue;
            Polyline outline = new Polyline { Layer = LabelLayer, Closed = true };
            outline.AddVertexAt(0, new Point2d(cell[0], cell[1]), 0, 0, 0);
            outline.AddVertexAt(1, new Point2d(cell[2], cell[1]), 0, 0, 0);
            outline.AddVertexAt(2, new Point2d(cell[2], cell[3]), 0, 0, 0);
            outline.AddVertexAt(3, new Point2d(cell[0], cell[3]), 0, 0, 0);
            btr.AppendEntity(outline);
            tr.AddNewlyCreatedDBObject(outline, true);
        }
        if (grid.Width > 0 && lines != "Off")
        {
            Line band = new Line(new Point3d(0, grid.BandTop, 0), new Point3d(grid.Width, grid.BandTop, 0))
            {
                Layer = LabelLayer,
            };
            btr.AppendEntity(band);
            tr.AddNewlyCreatedDBObject(band, true);
        }

        foreach (GridLabelDTO label in grid.Labels)
        {
            if (label.At == null || label.At.Count < 2) continue;
            MText text = new MText
            {
                Contents = label.Text,
                Location = new Point3d(label.At[0], label.At[1], 0),
                TextHeight = label.Height > 0 ? label.Height : 500,
                Layer = LabelLayer,
            };
            btr.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);
        }
    }

    /// <summary>
    /// Bring the pictures of earlier states in line with the record: one for
    /// every substep done before the state in hand, and none for anything else.
    ///
    /// The record is the whole of the rule, so the same call is right after a
    /// step, going back, going forward, switching branch or opening the job
    /// again - a state gone back past loses its picture, and one reached by a
    /// run that never drew it gets one, with nothing special for either.
    ///
    /// A picture is a block, named for the state and the direction it was laid
    /// out in. A state never changes once made, so a block already in the
    /// drawing is used again rather than fetched, and switching direction and
    /// back costs nothing the second time.
    /// </summary>
    public static async Task SyncSnapshots(Document doc, int projectId, GridDTO? grid,
                                           AutoDrawRecordDTO? record, AutoDrawConfigDTO? config)
    {
        Database db = doc.Database;
        var wanted = new List<string>();
        var toFetch = new List<(string name, int artifact)>();

        if (grid != null && grid.Mode == "stack" && record?.Steps != null && config?.Steps != null)
        {
            // The server's columns, not the config's substeps: a project step
            // has no column, so counting every substep would put every picture
            // after the first project step one column out.
            for (int here = 0; here < grid.Columns.Count && here < grid.Column; here++)
            {
                List<string> where = grid.Columns[here];
                if (where == null || where.Count < 2) continue;
                if (!record.Steps.TryGetValue(where[0], out AutoDrawStepStatusDTO? done)) continue;
                if (done?.Substeps == null
                    || !done.Substeps.TryGetValue(where[1], out AutoDrawSubstepStatusDTO? state)
                    || state?.ArtifactId == null) continue;

                string name = SnapshotPrefix + state.ArtifactId + "_" + grid.Direction;
                wanted.Add(name);
                if (!HasBlock(db, name)) toFetch.Add((name, state.ArtifactId.Value));
            }
        }

        // Fetched before the drawing is touched: a call to the server is not
        // something to hold the document locked across.
        var fetched = new List<(string name, string dxf)>();
        foreach (var (name, artifact) in toFetch)
        {
            DrawingAtDTO? drawing = await autodraw.AutoDraw.DrawingAt(projectId, artifact);
            if (drawing?.Success == true && !string.IsNullOrEmpty(drawing.Dxf))
                fetched.Add((name, drawing.Dxf));
        }
        TempTiming.Lap($"c5 snapshots fetch ({toFetch.Count} of {wanted.Count})"); // TEMP TIMING

        using (doc.LockDocument())
        {
            foreach (var (name, dxf) in fetched) DefineBlock(db, name, dxf);
            TempTiming.Lap($"c6 snapshots define ({fetched.Count})"); // TEMP TIMING

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                AutoDrawVisualizer.EnsureLayer(tr, db, HistoryLayer);
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var shown = new HashSet<string>();
                foreach (ObjectId id in ms)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference placed
                        || placed.Layer != HistoryLayer) continue;
                    string name = placed.Name;
                    if (wanted.Contains(name) && shown.Add(name)) continue;
                    placed.UpgradeOpen();
                    placed.Erase();
                }

                foreach (string name in wanted)
                {
                    if (shown.Contains(name) || !bt.Has(name)) continue;
                    BlockReference picture = new BlockReference(Point3d.Origin, bt[name]) { Layer = HistoryLayer };
                    ms.AppendEntity(picture);
                    tr.AddNewlyCreatedDBObject(picture, true);
                }
                tr.Commit();
            }
        }
        TempTiming.Lap("c7 snapshots place"); // TEMP TIMING
    }

    /// <summary>Look at the cells the state in hand occupies, with a margin.</summary>
    public static void Follow(Editor ed, GridDTO? grid)
    {
        if (grid?.Focus == null || grid.Focus.Count < 4) return;
        double margin = grid.Cell * 0.05;
        double x0 = grid.Focus[0] - margin, y0 = grid.Focus[1] - margin;
        double x1 = grid.Focus[2] + margin, y1 = grid.Focus[3] + margin;

        using (ViewTableRecord view = ed.GetCurrentView())
        {
            double width = x1 - x0, height = y1 - y0;
            double aspect = view.Width / Math.Max(view.Height, 1e-9);
            if (width / height < aspect) width = height * aspect; else height = width / aspect;
            view.CenterPoint = new Point2d((x0 + x1) / 2, (y0 + y1) / 2);
            view.Width = width;
            view.Height = height;
            view.ViewDirection = Vector3d.ZAxis;
            view.Target = Point3d.Origin;
            ed.SetCurrentView(view);
        }
    }

    private static bool HasBlock(Database db, string name)
    {
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            bool has = bt.Has(name);
            tr.Commit();
            return has;
        }
    }

    /// <summary>Make a block of one state's drawing, as the server laid it out.</summary>
    private static void DefineBlock(Database db, string name, string base64)
    {
        string path = Path.Combine(Path.GetTempPath(), $"autodraw_snap_{Guid.NewGuid():N}.dxf");
        File.WriteAllBytes(path, Convert.FromBase64String(base64));
        try
        {
            using (Database source = new Database(false, true))
            {
                source.DxfIn(path, null);
                db.Insert(name, source, true);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
