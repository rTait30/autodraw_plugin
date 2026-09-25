using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace autodraw_plugin.Services;

/// <summary>
/// Rebuilds the drawing's groups after a redraw, one per drawn piece.
///
/// A group refers to its members by their handles, which only survive a file
/// being opened. The redraw contract parses the server's DXF and clones the
/// entities into a document that is already open, so they arrive with new
/// handles and the groups the server sent refer to nothing.
///
/// Nothing is needed from the server to rebuild them. Every entity it owns
/// carries its own piece number in its AUTODRAW XDATA, so once the entities are
/// drawn, bucketing them by that number and making one group per bucket is the
/// whole job. It is the same loop whatever the drawing holds - `object` is the
/// automation's own idea rather than any product's, so this never has to know
/// what a panel or a doubler is.
///
/// The drawing must stay at R2000 or later. R12 has no objects section, so it
/// cannot carry a group at all, and anything that saves down to it drops them
/// with no error.
/// </summary>
public static class PieceGroupService
{
    public const string GroupPrefix = "PIECE_";

    /// <summary>The XDATA pair that says which piece an entity belongs to.</summary>
    private const string ObjectMarker = "object:=";

    /// <summary>
    /// Replace every PIECE_ group with one built from what is in modelspace
    /// now. Returns how many pieces were found. Runs inside the caller's
    /// transaction, after the import.
    /// </summary>
    public static int Rebuild(Transaction tr, Database db)
    {
        var pieces = new Dictionary<string, ObjectIdCollection>(StringComparer.Ordinal);

        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        foreach (ObjectId id in ms)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null) continue;

            string piece = PieceOf(ent);
            if (piece == null) continue;

            if (!pieces.TryGetValue(piece, out ObjectIdCollection members))
            {
                members = new ObjectIdCollection();
                pieces[piece] = members;
            }
            members.Add(id);
        }

        DBDictionary groups = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);

        // Replace rather than add to. Last redraw's groups still stand, holding
        // entities that have since been erased and the names these want; left
        // alone they would pile up one dead set per step.
        var stale = new List<ObjectId>();
        foreach (DBDictionaryEntry entry in groups)
        {
            if (entry.Key.StartsWith(GroupPrefix, StringComparison.Ordinal)) stale.Add(entry.Value);
        }
        foreach (ObjectId id in stale)
        {
            ((Group)tr.GetObject(id, OpenMode.ForWrite)).Erase();
        }

        foreach (KeyValuePair<string, ObjectIdCollection> piece in pieces)
        {
            string name = GroupPrefix + piece.Key;

            // Selectable, which is the point of the exercise: picking one of a
            // piece's seventeen entities takes hold of all of them, so nothing
            // is left behind at the old position when it is dragged.
            Group group = new Group(name, true);
            groups.SetAt(name, group);
            tr.AddNewlyCreatedDBObject(group, true);
            group.Append(piece.Value);
        }

        return pieces.Count;
    }

    /// <summary>
    /// The piece an entity belongs to, or null for one belonging to none.
    ///
    /// XDATA carries a pair to a string: `name=text` where the value is a
    /// string, `name:=json` where it is anything else. The marker is required
    /// rather than inferred, because a panel id really is the string "P1" and
    /// guessing would hand it back as something else; an object id is always a
    /// number, so only `object:=` is one. Its text is taken as written, which
    /// is what makes the name match the one the server writes.
    ///
    /// Geometry the server does not own carries no AUTODRAW XDATA at all, and
    /// some that it does own has no `object` on purpose - the fabric roll and
    /// its margins, which must not move when a panel is dragged.
    /// </summary>
    private static string PieceOf(Entity ent)
    {
        using (ResultBuffer rb = ent.GetXDataForApplication(DxfTransferService.OwnedAppId))
        {
            if (rb == null) return null;

            foreach (TypedValue value in rb)
            {
                if (value.TypeCode != 1000) continue;

                string pair = value.Value as string;
                if (pair == null || !pair.StartsWith(ObjectMarker, StringComparison.Ordinal)) continue;

                string number = pair.Substring(ObjectMarker.Length).Trim();
                return number.Length == 0 ? null : number;
            }
        }
        return null;
    }
}
