using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;

namespace autodraw_plugin.Services;

/// <summary>
/// Moves geometry between the drawing and the server as DXF.
///
/// The server owns every entity carrying AUTODRAW XDATA and redraws them from
/// scratch each cycle, so the sequence is always erase-then-import. Anything
/// carrying another vendor's appid - MPanel's mesh above all - is never erased:
/// the server cannot regenerate it and does not send it back.
/// </summary>
public static class DxfTransferService
{
    public const string OwnedAppId = "AUTODRAW";

    /// <summary>
    /// Erase the entities the server owns, ready for its replacements.
    /// Returns how many were removed. Runs inside the caller's transaction.
    /// </summary>
    public static int EraseOwned(Transaction tr, Database db)
    {
        var doomed = new List<ObjectId>();

        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        foreach (ObjectId id in ms)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null) continue;

            using (ResultBuffer rb = ent.GetXDataForApplication(OwnedAppId))
            {
                if (rb == null) continue;

                // A full-scope copy stamps a fid on everything, MPanel's mesh
                // included. Only the server's own entities may be erased - it
                // cannot redraw anything marked owner=cad.
                bool foreign = false;
                foreach (TypedValue value in rb)
                {
                    if (value.TypeCode == 1000 && (value.Value as string) == "owner=cad")
                    {
                        foreign = true;
                        break;
                    }
                }
                if (!foreign) doomed.Add(id);
            }
        }

        foreach (ObjectId id in doomed)
        {
            Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            ent.Erase();
        }
        return doomed.Count;
    }

    /// <summary>
    /// How many entities in the drawing carry the server's stamp. Compared with
    /// the record's own count, this says whether the drawing is in step with
    /// the server or whether it needs an ADSTART to resync.
    /// </summary>
    public static int CountStamped(Database db)
    {
        int count = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null) continue;
                using (ResultBuffer rb = ent.GetXDataForApplication(OwnedAppId))
                {
                    if (rb != null) count++;
                }
            }
            tr.Commit();
        }
        return count;
    }

    /// <summary>
    /// Erase everything in modelspace except the given layers - a full resync,
    /// where the record is authoritative and the drawing is rebuilt from it.
    /// Unlike EraseOwned this also removes MPanel's geometry, so it must only
    /// be used when the server is sending the whole drawing back.
    /// </summary>
    public static int EraseAllExcept(Transaction tr, Database db, IEnumerable<string> keepLayers)
    {
        var kept = new HashSet<string>(keepLayers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var doomed = new List<ObjectId>();

        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        foreach (ObjectId id in ms)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null || kept.Contains(ent.Layer)) continue;
            doomed.Add(id);
        }

        foreach (ObjectId id in doomed)
        {
            ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
        }
        return doomed.Count;
    }

    /// <summary>
    /// Write modelspace out to a DXF for submission, skipping the given layers.
    ///
    /// Deliberately permissive: everything except the decoration goes, and the
    /// server decides what it will adopt. That way its ignored count measures
    /// how much of a real drawing the contract does not cover, instead of just
    /// echoing back whatever the plugin already filtered.
    /// </summary>
    public static int ExportModelspace(Database db, string path, IEnumerable<string> excludeLayers)
    {
        var excluded = new HashSet<string>(excludeLayers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var ids = new ObjectIdCollection();

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent == null || excluded.Contains(ent.Layer)) continue;
                ids.Add(id);
            }
            tr.Commit();
        }

        using (Database dest = new Database(true, true))
        {
            if (ids.Count > 0)
            {
                ObjectId destOwner;
                using (Transaction tr = dest.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(dest.BlockTableId, OpenMode.ForRead);
                    destOwner = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }

                IdMapping mapping = new IdMapping();
                db.WblockCloneObjects(ids, destOwner, mapping, DuplicateRecordCloning.Replace, false);
            }

            dest.DxfOut(path, 16, DwgVersion.Current);
        }
        return ids.Count;
    }

    /// <summary>
    /// Import a base64 DXF from the server into the drawing's current space.
    /// Returns the number of entities laid in.
    /// </summary>
    public static int ImportBase64(Database db, string base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return 0;

        string path = Path.Combine(Path.GetTempPath(), $"autodraw_{Guid.NewGuid():N}.dxf");
        File.WriteAllBytes(path, Convert.FromBase64String(base64));

        try
        {
            return ImportFile(db, path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Read a DXF into a side database and clone its modelspace entities into
    /// this drawing. Cloning rather than inserting keeps them as ordinary
    /// entities - not a block reference - and carries their XDATA and layers
    /// across, which is what makes the fid stamps survive the round trip.
    /// </summary>
    public static int ImportFile(Database db, string path)
    {
        using (Database source = new Database(false, true))
        {
            source.DxfIn(path, null);

            var ids = new ObjectIdCollection();
            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
                BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms) ids.Add(id);
                tr.Commit();
            }

            if (ids.Count == 0) return 0;

            // Modelspace explicitly, not CurrentSpaceId: EraseOwned works on
            // modelspace, and importing into paperspace would leave the two
            // halves of the redraw in different places.
            ObjectId destination;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                destination = bt[BlockTableRecord.ModelSpace];
                tr.Commit();
            }

            IdMapping mapping = new IdMapping();
            source.WblockCloneObjects(
                ids,
                destination,
                mapping,
                DuplicateRecordCloning.Replace,
                false);

            return ids.Count;
        }
    }
}
