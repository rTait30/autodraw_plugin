using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Models.Operations;

namespace autodraw_plugin.Services;

/// <summary>
/// A project's operation history as a grid of pictures beside the drawing in
/// hand (ADOPTIONS Layout Grid): one lane per operation along the history, in
/// the Direction set, and across it one lane per job item, the job-wide rows
/// in a lane of their own first.
///
/// The server hands out each row of the history (`states=1`) as only what it
/// drew, where it was made; it places nothing. An operation over the job's
/// items is a row per item, all but its last `partial`, so a run of partial
/// rows and the row ending it are one operation - one lane, a cell per item.
///
/// Like the stack's pictures these are block references on AUTODRAW_HISTORY,
/// so they are decoration: never submitted, never grouped, never counted. The
/// drawing in hand stays live and where it was made.
/// </summary>
public static class OperationGridService
{
    /// <summary>Lay the grid out, replacing the pictures there were. Nothing without states.</summary>
    public static void Lay(Document doc, List<OperationStateDTO>? states, string direction)
    {
        Database db = doc.Database;
        if (states == null || states.Count == 0) return;

        // One lane per operation: the partial rows and the row that ends them.
        var operations = new List<(string label, List<OperationStateDTO> rows)>();
        var waiting = new List<OperationStateDTO>();
        foreach (OperationStateDTO state in states)
        {
            waiting.Add(state);
            if (state.Partial) continue;
            operations.Add((Label(state), waiting));
            waiting = new List<OperationStateDTO>();
        }
        if (waiting.Count > 0) operations.Add((Label(waiting[^1]), waiting));

        // The job-wide lane first, then the items in the order of their ids.
        bool jobWide = states.Any(state => state.ItemId == null);
        List<int?> items = states.Where(state => state.ItemId != null)
            .Select(state => state.ItemId).Distinct().OrderBy(id => id).ToList();
        if (jobWide) items.Insert(0, null);

        // Defined once each: a row's drawing never changes once saved.
        foreach (OperationStateDTO state in states)
        {
            string name = BlockName(state);
            if (!string.IsNullOrEmpty(state.Drawing) && !GridService.HasBlock(db, name))
                GridService.DefineBlock(db, name, state.Drawing);
        }

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            AutoDrawVisualizer.EnsureLayer(tr, db, GridService.HistoryLayer);
            AutoDrawVisualizer.EnsureLayer(tr, db, AutoDrawVisualizer.CaptionLayer);
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // What each row drew, measured, so every cell can be as big as the
            // biggest and the grid reads as a grid.
            var extents = new Dictionary<int, Extents3d>();
            foreach (OperationStateDTO state in states)
            {
                string name = BlockName(state);
                if (!bt.Has(name)) continue;
                Extents3d? box = Measure(tr, (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead));
                if (box.HasValue) extents[state.ArtifactId] = box.Value;
            }
            if (extents.Count == 0) { tr.Commit(); return; }

            double width = extents.Values.Max(box => box.MaxPoint.X - box.MinPoint.X);
            double height = extents.Values.Max(box => box.MaxPoint.Y - box.MinPoint.Y);
            double gap = Math.Max(Math.Max(width, height) * 0.15, 1.0);
            double cellW = width + gap, cellH = height + gap;
            double text = Math.Max(Math.Min(cellW, cellH) * 0.04, 1.0);

            // Beside the live drawing - to its right, or under it running down -
            // leaving room for the lanes' captions.
            bool down = string.Equals(direction, "Down", StringComparison.OrdinalIgnoreCase);
            Extents3d? live = Live(tr, ms);
            double left = live.HasValue
                ? (down ? live.Value.MinPoint.X : live.Value.MaxPoint.X + gap)
                : 0.0;
            double top = live.HasValue
                ? (down ? live.Value.MinPoint.Y - gap : live.Value.MaxPoint.Y)
                : 0.0;
            // The captions take the first row and column of cells' worth of text.
            double captionW = text * 14, captionH = text * 3;
            double x0 = left + captionW, y0 = top - captionH;

            // Operations run the Direction set; items run across them.
            Point2d CellAt(int operation, int item) => down
                ? new Point2d(x0 + item * cellW, y0 - (operation + 1) * cellH)
                : new Point2d(x0 + operation * cellW, y0 - (item + 1) * cellH);

            for (int op = 0; op < operations.Count; op++)
            {
                Point2d at = CellAt(op, 0);
                Caption(tr, ms, down
                    ? new Point3d(left, at.Y + cellH / 2, 0)
                    : new Point3d(at.X, top - text * 1.5, 0), $"{op + 1}. {operations[op].label}", text);

                foreach (OperationStateDTO state in operations[op].rows)
                {
                    if (!extents.TryGetValue(state.ArtifactId, out Extents3d box)) continue;
                    Point2d cell = CellAt(op, items.IndexOf(state.ItemId));
                    // Its lower-left corner in the cell's, a half gap in.
                    var shift = new Vector3d(cell.X + gap / 2 - box.MinPoint.X,
                                             cell.Y + gap / 2 - box.MinPoint.Y, 0);
                    var picture = new BlockReference(Point3d.Origin + shift, bt[BlockName(state)])
                    {
                        Layer = GridService.HistoryLayer,
                    };
                    ms.AppendEntity(picture);
                    tr.AddNewlyCreatedDBObject(picture, true);
                }
            }

            for (int item = 0; item < items.Count; item++)
            {
                Point2d at = CellAt(0, item);
                string label = items[item] == null ? "Job" : $"Item {items[item]}";
                Caption(tr, ms, down
                    ? new Point3d(at.X, top - text * 1.5, 0)
                    : new Point3d(left, at.Y + cellH / 2, 0), label, text);
            }
            tr.Commit();
        }
    }

    private static string Label(OperationStateDTO state)
    {
        string what = state.Operation ?? (state.Source == "submitted" ? "changed in CAD" : state.Source ?? "?");
        return state.Recipe != null ? $"{what} ({state.Recipe})" : what;
    }

    /// <summary>
    /// The block a row's picture is made of. Under the snapshot prefix, so a
    /// reset forgets it: the server reuses ids once a job's states are deleted.
    /// </summary>
    private static string BlockName(OperationStateDTO state) =>
        GridService.SnapshotPrefix + "OP" + state.ArtifactId;

    private static void Caption(Transaction tr, BlockTableRecord ms, Point3d at, string words, double height)
    {
        var caption = new DBText
        {
            Position = at,
            Height = height,
            TextString = words,
            Layer = AutoDrawVisualizer.CaptionLayer,
        };
        ms.AppendEntity(caption);
        tr.AddNewlyCreatedDBObject(caption, true);
    }

    private static Extents3d? Measure(Transaction tr, BlockTableRecord block)
    {
        Extents3d? box = null;
        foreach (ObjectId id in block)
            box = Grow(box, (tr.GetObject(id, OpenMode.ForRead) as Entity)?.Bounds);
        return box;
    }

    /// <summary>The drawing in hand: everything in modelspace but the decoration.</summary>
    private static Extents3d? Live(Transaction tr, BlockTableRecord ms)
    {
        var decoration = new HashSet<string>(AutoDrawVisualizer.DecorationLayers,
                                             StringComparer.OrdinalIgnoreCase);
        Extents3d? box = null;
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && !decoration.Contains(ent.Layer))
                box = Grow(box, ent.Bounds);
        }
        return box;
    }

    private static Extents3d? Grow(Extents3d? box, Extents3d? more)
    {
        if (!more.HasValue) return box;
        if (!box.HasValue) return more;
        Extents3d grown = box.Value;
        grown.AddExtents(more.Value);
        return grown;
    }
}
