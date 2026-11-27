using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Models.Operations;

namespace autodraw_plugin.Services;

/// <summary>
/// A loaded job's history drawn beside its drawing, as pictures of each state
/// on the layers it was drawn on (ADOPTIONS Layout Stack or Grid).
///
/// - Stack: one picture per operation along the Direction, oldest first.
/// - Grid: the same lane of operations, and across it one lane per job item,
///   the job-wide rows in a lane of their own first.
///
/// The server hands out each row of the history (`states=1`) as only what it
/// drew, where it was made; it places nothing. An operation over the job's
/// items is a row per item, all but its last `partial`, so a run of partial
/// rows and the row ending it are one operation.
///
/// A picture is a block reference to a block named `ADSNAP_OP&lt;artifact&gt;`,
/// its entities on their own layers with the AUTODRAW stamp taken off - it is
/// a picture, not the server's work. It is kept out of everything sent back
/// by what it is (`IsPicture`), not by a layer, so turning a layer off turns
/// it off in the pictures too. The drawing in hand stays live where it was made.
/// </summary>
public static class OperationGridService
{
    private const string Prefix = GridService.SnapshotPrefix + "OP";

    /// <summary>Whether a loaded job's history is drawn beside it, and so asked for.</summary>
    public static bool Shown => PluginSettings.Current.OperationsGrid
                                || string.Equals(PluginSettings.Current.Layout, "Stack",
                                                 StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an entity is one of these pictures, never to be sent to the server.</summary>
    public static bool IsPicture(Entity entity) =>
        entity is BlockReference placed && placed.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lay the history out, replacing the pictures there were. Nothing without
    /// states. `byItem` gives each item its own lane (Grid); otherwise each
    /// operation is one picture of everything it drew (Stack).
    /// </summary>
    public static void Lay(Document doc, List<OperationStateDTO>? states, string direction, bool byItem)
    {
        Database db = doc.Database;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            Clear(tr, db);
            tr.Commit();
        }
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

        // Across: the job-wide lane first, then the items in the order of their
        // ids - or, stacked, a single lane for everything.
        List<int?> items = new() { null };
        if (byItem)
        {
            items = states.Where(state => state.ItemId != null)
                .Select(state => state.ItemId).Distinct().OrderBy(id => id).ToList();
            if (states.Any(state => state.ItemId == null)) items.Insert(0, null);
        }
        int LaneOf(OperationStateDTO state) => byItem ? items.IndexOf(state.ItemId) : 0;

        // Defined once each: a row's drawing never changes once saved.
        foreach (OperationStateDTO state in states)
        {
            string name = BlockName(state);
            if (!string.IsNullOrEmpty(state.Drawing) && !GridService.HasBlock(db, name))
            {
                GridService.DefineBlock(db, name, state.Drawing);
                Unstamp(db, name);
            }
        }

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            AutoDrawVisualizer.EnsureLayer(tr, db, AutoDrawVisualizer.CaptionLayer);
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // What each row drew, measured, so every cell can be as big as the
            // biggest and the pictures line up.
            var extents = new Dictionary<int, Extents3d>();
            foreach (OperationStateDTO state in states)
            {
                string name = BlockName(state);
                if (!bt.Has(name)) continue;
                Extents3d? box = Measure(tr, (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead));
                if (box.HasValue) extents[state.ArtifactId] = box.Value;
            }
            if (extents.Count == 0) { tr.Commit(); return; }

            // An operation's cell holds all its rows when they share a lane, so
            // it is measured as their union.
            Extents3d? CellBox(List<OperationStateDTO> rows) => rows
                .Where(row => extents.ContainsKey(row.ArtifactId))
                .Aggregate((Extents3d?)null, (box, row) => Grow(box, extents[row.ArtifactId]));
            var boxes = operations.SelectMany(op => byItem
                    ? op.rows.Where(row => extents.ContainsKey(row.ArtifactId)).Select(row => (Extents3d?)extents[row.ArtifactId])
                    : new[] { CellBox(op.rows) })
                .Where(box => box.HasValue).Select(box => box!.Value).ToList();
            double width = boxes.Max(box => box.MaxPoint.X - box.MinPoint.X);
            double height = boxes.Max(box => box.MaxPoint.Y - box.MinPoint.Y);
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
            double captionW = byItem ? text * 14 : 0, captionH = text * 3;
            double x0 = left + captionW, y0 = top - captionH;

            // Operations run the Direction set; items run across them.
            Point2d CellAt(int operation, int lane) => down
                ? new Point2d(x0 + lane * cellW, y0 - (operation + 1) * cellH)
                : new Point2d(x0 + operation * cellW, y0 - (lane + 1) * cellH);

            for (int op = 0; op < operations.Count; op++)
            {
                Point2d at = CellAt(op, 0);
                Caption(tr, ms, down
                    ? new Point3d(byItem ? left : at.X, byItem ? at.Y + cellH / 2 : at.Y + cellH - text * 1.5, 0)
                    : new Point3d(at.X, top - text * 1.5, 0), $"{op + 1}. {operations[op].label}", text);

                Extents3d? together = byItem ? null : CellBox(operations[op].rows);
                foreach (OperationStateDTO state in operations[op].rows)
                {
                    if (!extents.TryGetValue(state.ArtifactId, out Extents3d own)) continue;
                    Extents3d box = together ?? own;
                    Point2d cell = CellAt(op, LaneOf(state));
                    // The cell's content by its lower-left corner, a half gap in.
                    var shift = new Vector3d(cell.X + gap / 2 - box.MinPoint.X,
                                             cell.Y + gap / 2 - box.MinPoint.Y, 0);
                    var picture = new BlockReference(Point3d.Origin + shift, bt[BlockName(state)]) { Layer = "0" };
                    ms.AppendEntity(picture);
                    tr.AddNewlyCreatedDBObject(picture, true);
                }
            }

            if (byItem)
            {
                for (int lane = 0; lane < items.Count; lane++)
                {
                    Point2d at = CellAt(0, lane);
                    string label = items[lane] == null ? "Job" : $"Item {items[lane]}";
                    Caption(tr, ms, down
                        ? new Point3d(at.X, top - text * 1.5, 0)
                        : new Point3d(left, at.Y + cellH / 2, 0), label, text);
                }
            }
            tr.Commit();
        }
    }

    /// <summary>Erase every picture placed; the definitions stay, a row never changing.</summary>
    public static void Clear(Transaction tr, Database db)
    {
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && IsPicture(ent))
            {
                ent.UpgradeOpen();
                ent.Erase();
            }
        }
    }

    /// <summary>
    /// Take the AUTODRAW stamp off a picture's entities. It says "the server
    /// drew this and can redraw it", which a picture is not - and a picture
    /// exploded with it on would carry the live entities' own ids.
    /// </summary>
    private static void Unstamp(Database db, string name)
    {
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(name)) { tr.Commit(); return; }
            var block = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
                if (ent.GetXDataForApplication("AUTODRAW") == null) continue;
                ent.UpgradeOpen();
                // Naming the application alone removes its data.
                ent.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, "AUTODRAW"));
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
    private static string BlockName(OperationStateDTO state) => Prefix + state.ArtifactId;

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

    /// <summary>The drawing in hand: everything in modelspace but the decoration and the pictures.</summary>
    private static Extents3d? Live(Transaction tr, BlockTableRecord ms)
    {
        var decoration = new HashSet<string>(AutoDrawVisualizer.DecorationLayers,
                                             StringComparer.OrdinalIgnoreCase);
        Extents3d? box = null;
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && !decoration.Contains(ent.Layer) && !IsPicture(ent))
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
