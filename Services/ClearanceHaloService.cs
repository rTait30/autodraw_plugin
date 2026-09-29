using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace autodraw_plugin.Services;

/// <summary>
/// Draws the guide a designer nests against: a halo around each piece, at half
/// the gap the server says two pieces need between them.
///
/// Two halos meeting is exactly that gap. Half around each rather than the
/// whole of it around one, because it is symmetric - nobody has to remember
/// which of two pieces owns the space between them.
///
/// **This is a guide and never a verdict.** The server measures the real
/// outlines and refuses a nest that is too tight; it never sees these, because
/// they are on a decoration layer that is excluded from every submission. So a
/// halo that is stale, deleted, or a hair off at a corner cannot produce a
/// wrong answer - only a wrong-looking drawing. That is the whole reason the
/// guide is drawn here and the rule is enforced there.
///
/// The halos carry **no AUTODRAW XDATA**. That stamp means "the server drew
/// this and can redraw it", and these are the plugin's own. Keeping it off them
/// also keeps them out of `CountStamped`, which counts the server's entities to
/// decide whether the drawing is still in step with the record - a halo
/// counted there would look like drift and stop the next step.
/// </summary>
public static class ClearanceHaloService
{
    /// <summary>
    /// Rebuild every halo from what is in modelspace now, and add each to its
    /// piece's group so it travels when the piece is dragged. Runs inside the
    /// caller's transaction, **after** PieceGroupService, whose groups it
    /// appends to. Returns how many halos were drawn.
    ///
    /// `gap` is the whole clearance the server published; the halo is half.
    /// Nothing is drawn when the server has not said, because a guide drawn to
    /// a number this side invented is worse than no guide at all - it would be
    /// believed.
    /// </summary>
    public static int Rebuild(Transaction tr, Database db, double? gap)
    {
        AutoDrawVisualizer.EnsureLayer(tr, db, AutoDrawVisualizer.ToleranceLayer,
                                       AutoDrawVisualizer.ToleranceColor);

        // Replace, do not accumulate - the same rule the groups follow. The
        // ordinary erase spares these, because they carry no AUTODRAW stamp,
        // so without this every redraw would leave the last set behind.
        AutoDrawVisualizer.ClearLayer(tr, db, AutoDrawVisualizer.ToleranceLayer);

        if (gap == null || gap.Value <= 0.0) return 0;
        double half = gap.Value / 2.0;

        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        // What to draw a halo around is the piece's outline, and a piece's
        // outline is the closed curve in it. Chosen on that rather than on what
        // the server calls the entity: this plugin does not read roles and
        // should not start, and "the closed one" is true of a panel and of a
        // doubler alike without either being named here.
        var outlines = new Dictionary<string, List<Polyline>>(StringComparer.Ordinal);
        foreach (ObjectId id in ms)
        {
            Polyline outline = tr.GetObject(id, OpenMode.ForRead) as Polyline;
            if (outline == null || !outline.Closed) continue;

            string piece = PieceGroupService.PieceOf(outline);
            if (piece == null) continue;

            if (!outlines.TryGetValue(piece, out List<Polyline> mine))
            {
                mine = new List<Polyline>();
                outlines[piece] = mine;
            }
            mine.Add(outline);
        }

        DBDictionary groups = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead);
        int drawn = 0;

        foreach (KeyValuePair<string, List<Polyline>> piece in outlines)
        {
            var added = new ObjectIdCollection();
            foreach (Polyline outline in piece.Value)
            {
                foreach (Entity halo in Around(outline, half))
                {
                    halo.Layer = AutoDrawVisualizer.ToleranceLayer;
                    ms.AppendEntity(halo);
                    tr.AddNewlyCreatedDBObject(halo, true);
                    added.Add(halo.ObjectId);
                    drawn++;
                }
            }
            if (added.Count == 0) continue;

            // Into the piece's own group, so dragging the piece drags what the
            // designer is keeping apart. The group is the one the piece
            // service just made; a piece with none is skipped rather than
            // given one here, because making groups is that service's job.
            string name = PieceGroupService.GroupPrefix + piece.Key;
            if (!groups.Contains(name)) continue;
            Group group = tr.GetObject(groups.GetAt(name), OpenMode.ForWrite) as Group;
            if (group != null) group.Append(added);
        }

        return drawn;
    }

    /// <summary>
    /// The outline grown outwards by a distance, as whatever curves that takes.
    ///
    /// **Which way is outwards depends on the direction the outline was drawn**,
    /// and that is not knowable from here: a piece the server mirrored comes
    /// back wound the other way, so a sign chosen once would shrink every piece
    /// on half of all jobs and the halos would sit inside the fabric where
    /// nothing could see them. So both are offset and the larger is kept,
    /// which needs no winding rule and cannot be got backwards.
    ///
    /// An offset can fail or come back in pieces where a curve is tighter than
    /// the distance. Failing quietly is right here: the halo is a guide, the
    /// server's check is the verdict, and a piece with no halo is a piece the
    /// designer nests the old way rather than a job that stops.
    /// </summary>
    private static List<Entity> Around(Polyline outline, double distance)
    {
        DBObjectCollection outward = Offset(outline, distance);
        DBObjectCollection inward = Offset(outline, -distance);

        DBObjectCollection bigger = Enclosed(outward) >= Enclosed(inward) ? outward : inward;
        DBObjectCollection discarded = ReferenceEquals(bigger, outward) ? inward : outward;

        foreach (DBObject spare in discarded) spare.Dispose();

        var halo = new List<Entity>();
        foreach (DBObject curve in bigger)
        {
            if (curve is Entity ent) halo.Add(ent);
            else curve.Dispose();
        }
        return halo;
    }

    private static DBObjectCollection Offset(Polyline outline, double distance)
    {
        try
        {
            return outline.GetOffsetCurves(distance);
        }
        catch (Exception)
        {
            return new DBObjectCollection();
        }
    }

    /// <summary>How much a set of curves encloses, for telling the two offsets apart.</summary>
    private static double Enclosed(DBObjectCollection curves)
    {
        double total = 0.0;
        foreach (DBObject curve in curves)
        {
            if (curve is Polyline closed && closed.Closed)
            {
                try { total += Math.Abs(closed.Area); }
                catch (Exception) { }
            }
        }
        return total;
    }
}
