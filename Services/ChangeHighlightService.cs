using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace autodraw_plugin.Services;

/// <summary>
/// Shows what the last step changed, so somebody watching can tell what it did.
///
/// Every entity the server draws carries its `fid`, the record's own id for it,
/// and that survives a redraw. So a snapshot of fid and shape taken before the
/// erase, compared with the drawing after the import, says what is new and what
/// moved. Those are highlighted with AutoCAD's own highlight, which leaves every
/// entity in its layer's colour - the colour is how a designer reads a layer,
/// and repainting it to mark a change would hide exactly that.
///
/// A highlight is display only: it is never saved, never submitted, and a regen
/// wipes it, which is why it is applied after the redraw's regen. It is cleared
/// again by the next redraw, or as soon as the designer starts a command or
/// picks something - by then they have seen it, and a highlight that lingers
/// looks like a selection.
///
/// Removed entities are not shown: they are gone by the time there is anything
/// to highlight.
/// </summary>
public static class ChangeHighlightService
{
    private const string FidMarker = "fid:=";
    private const string FidTextMarker = "fid=";

    private static readonly List<ObjectId> Lit = new();

    /// <summary>The grid the snapshot was taken in, to allow for the column moving.</summary>
    private static autodraw_plugin.Models.AutoDraw.GridDTO? _columnBefore;
    private static Document? _watched;

    /// <summary>
    /// fid to shape, for every entity the server owns. Taken before the erase.
    /// Clears any highlight still showing, since the drawing is about to change
    /// under it.
    /// </summary>
    public static Dictionary<string, string> Snapshot(Database db)
    {
        Clear();
        _columnBefore = GridService.Last;

        var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var (fid, ent) in Stamped(tr, db))
            {
                shapes[fid] = Shape(ent);
            }
            tr.Commit();
        }
        return shapes;
    }

    /// <summary>
    /// Highlight whatever is new or different since the snapshot. Call after
    /// the redraw's regen. Returns how many entities were highlighted.
    /// </summary>
    public static int Show(Document doc, Dictionary<string, string> before)
    {
        Clear();
        if (before == null) return 0;

        Database db = doc.Database;
        var moved = ColumnMove(_columnBefore, GridService.Last);
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            foreach (var (fid, ent) in Stamped(tr, db))
            {
                // Stacked, the whole state moves along a column every step, so
                // a position is compared as it was before the move. The project
                // band stays where it is.
                var (dx, dy) = IsProject(ent) ? (0.0, 0.0) : moved;
                if (before.TryGetValue(fid, out string? shape) && shape == Shape(ent, dx, dy)) continue;
                ent.Highlight();
                Lit.Add(ent.ObjectId);
            }
            tr.Commit();
        }

        if (Lit.Count > 0)
        {
            _watched = doc;
            doc.CommandWillStart += OnInteraction;
            doc.ImpliedSelectionChanged += OnInteraction;
            doc.Editor.UpdateScreen();
        }
        return Lit.Count;
    }

    /// <summary>Take every highlight down again.</summary>
    public static void Clear()
    {
        if (_watched != null)
        {
            _watched.CommandWillStart -= OnInteraction;
            _watched.ImpliedSelectionChanged -= OnInteraction;
        }

        if (Lit.Count > 0 && _watched != null)
        {
            using (Transaction tr = _watched.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (ObjectId id in Lit)
                {
                    if (id.IsNull || id.IsErased || !id.IsValid) continue;
                    if (tr.GetObject(id, OpenMode.ForRead) is Entity ent) ent.Unhighlight();
                }
                tr.Commit();
            }
        }

        Lit.Clear();
        _watched = null;
    }

    private static void OnInteraction(object? sender, EventArgs e) => Clear();

    /// <summary>Modelspace entities that carry a fid, with that fid.</summary>
    private static IEnumerable<(string, Entity)> Stamped(Transaction tr, Database db)
    {
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent) continue;
            string? fid = FidOf(ent);
            if (fid != null) yield return (fid, ent);
        }
    }

    /// <summary>The fid pair's value as written, or null for an unstamped entity.</summary>
    private static string? FidOf(Entity ent)
    {
        using (ResultBuffer rb = ent.GetXDataForApplication(DxfTransferService.OwnedAppId))
        {
            if (rb == null) return null;
            foreach (TypedValue value in rb)
            {
                if (value.TypeCode != 1000 || value.Value is not string pair) continue;
                if (pair.StartsWith(FidMarker, StringComparison.Ordinal))
                    return pair.Substring(FidMarker.Length).Trim();
                if (pair.StartsWith(FidTextMarker, StringComparison.Ordinal))
                    return pair.Substring(FidTextMarker.Length).Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Enough of an entity to tell whether a step changed it: its kind, layer
    /// and colour, where it sits, and what it says if it is text. Rounded, so
    /// a DXF round trip's last digits do not count as a change.
    /// </summary>
    /// <summary>How far the state in hand moved between two grids, as a shift back.</summary>
    private static (double, double) ColumnMove(autodraw_plugin.Models.AutoDraw.GridDTO? before, autodraw_plugin.Models.AutoDraw.GridDTO? after)
    {
        if (before == null || after == null || before.Mode != "stack" || after.Mode != "stack"
            || before.Direction != after.Direction) return (0.0, 0.0);
        double step = (after.Column - before.Column) * after.Cell;
        return after.Direction == "down" ? (0.0, step) : (-step, 0.0);
    }

    private static bool IsProject(Entity ent)
    {
        using (ResultBuffer rb = ent.GetXDataForApplication(DxfTransferService.OwnedAppId))
        {
            if (rb == null) return false;
            foreach (TypedValue value in rb)
            {
                if (value.TypeCode == 1000 && value.Value as string == "frame=project") return true;
            }
        }
        return false;
    }

    private static string Shape(Entity ent, double dx = 0.0, double dy = 0.0)
    {
        string box = "";
        try
        {
            Extents3d extents = ent.GeometricExtents;
            Vector3d back = new Vector3d(dx, dy, 0);
            box = Point(extents.MinPoint + back) + "/" + Point(extents.MaxPoint + back);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Some entities have no extents (an empty text, a ray). Their kind,
            // layer and text still say enough.
        }

        string text = ent switch
        {
            DBText t => t.TextString,
            MText m => m.Contents,
            _ => "",
        };
        return ent.GetType().Name + "|" + ent.Layer + "|" + ent.ColorIndex + "|" + box + "|" + text;
    }

    private static string Point(Point3d p) =>
        Math.Round(p.X, 2) + "," + Math.Round(p.Y, 2) + "," + Math.Round(p.Z, 2);
}
