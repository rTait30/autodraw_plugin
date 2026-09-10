using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using autodraw_plugin.Models.Projects;
using autodraw_plugin.Models.AutoDraw;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;

using DBPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace autodraw_plugin.Services;

/// <summary>
/// Draws the workflow state into the drawing for the designer to read.
///
/// Everything here goes on the INFO layer, which the server's submission
/// whitelist does not include - so none of it is ever sent back or adopted as
/// geometry. It is decoration, and it is placed clear of the real drawing.
/// </summary>
public static class AutoDrawVisualizer
{
    public const string InfoLayer = "INFO";

    /// <summary>
    /// The board sits at a fixed spot rather than tracking the drawing's edge,
    /// so it stays where the designer last looked for it instead of moving
    /// every time the geometry grows.
    /// </summary>
    private static readonly Point3d BoardPosition = new Point3d(-30000, 0, 0);

    /// <summary>Clearance above the sail for the per-sail summary.</summary>
    private const double SummaryMargin = 3000.0;
    private const double SummaryHeight = 400.0;

    /// <summary>Create the INFO layer if it is missing. Non-plotting: it is not part of the job.</summary>
    public static void EnsureInfoLayer(Transaction tr, Database db)
    {
        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(InfoLayer)) return;

        lt.UpgradeOpen();
        LayerTableRecord layer = new LayerTableRecord
        {
            Name = InfoLayer,
            IsPlottable = false,
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>Erase the previous board so it can be redrawn from current state.</summary>
    public static void ClearInfoLayer(Transaction tr, Database db)
    {
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        var doomed = new List<ObjectId>();
        foreach (ObjectId id in ms)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent != null && ent.Layer == InfoLayer) doomed.Add(id);
        }
        foreach (ObjectId id in doomed)
        {
            ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
        }
    }

    /// <summary>Where the board sits. Fixed, so it does not wander between runs.</summary>
    public static Point3d BoardOrigin(Transaction tr, Database db)
    {
        return BoardPosition;
    }

    /// <summary>The extents of the real geometry, ignoring the INFO board.</summary>
    private static bool ContentExtents(Transaction tr, Database db, out double minX, out double maxY)
    {
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        bool any = false;
        minX = 0; maxY = 0;

        foreach (ObjectId id in ms)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent == null || ent.Layer == InfoLayer) continue;

            Extents3d ext;
            try { ext = ent.GeometricExtents; }
            catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }

            if (!any) { minX = ext.MinPoint.X; maxY = ext.MaxPoint.Y; any = true; }
            else
            {
                minX = Math.Min(minX, ext.MinPoint.X);
                maxY = Math.Max(maxY, ext.MaxPoint.Y);
            }
        }

        return any;
    }

    /// <summary>
    /// Write each cable section's length beside the sail.
    ///
    /// Drawn by the plugin from values the step recorded, not sent as geometry:
    /// anything the server drew on INFO would be excluded from the next
    /// submission, read back as deleted, and disappear. Derived numbers belong
    /// on the record, and the drawing renders them.
    /// </summary>
    public static void DrawCableSummary(Transaction tr, BlockTableRecord btr, Database db, AutoDrawRecordDTO record)
    {
        if (record?.Steps == null) return;

        var lines = new List<string>();
        foreach (var step in record.Steps.Values)
        {
            if (step?.Substeps == null) continue;
            foreach (var substep in step.Substeps.Values)
            {
                JToken sections = substep?.Metadata?["derived"]?["cableSections"];
                if (sections == null) continue;
                foreach (JToken section in sections)
                {
                    double length = section["lengthMm"]?.Value<double>() ?? 0.0;
                    lines.Add($"Cable {section["section"]}   {length:N0}mm");
                }
            }
        }
        if (lines.Count == 0) return;

        double minX, maxY;
        if (!ContentExtents(tr, db, out minX, out maxY)) return;

        MText text = new MText();
        text.Contents = "{\\C3;" + string.Join("\\P", lines) + "}";
        text.Location = new Point3d(minX, maxY + SummaryMargin, 0);
        text.TextHeight = SummaryHeight;
        text.Layer = InfoLayer;
        btr.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }

    public static void DrawStatusBoard(Transaction tr, BlockTableRecord btr, AutoDrawConfigDTO config, AutoDrawMetaDTO meta, Point3d startPt)
    {
        double currentY = startPt.Y;
        double stepGap = 1500;
        double textHeightStep = 800;
        double textHeightSub = 500;

        MText header = new MText();
        header.Contents = "{\\H1200;\\C3;Steps}";
        header.Location = new Point3d(startPt.X, currentY + 2000, 0);
        header.TextHeight = 1200;
        header.Layer = InfoLayer;
        btr.AppendEntity(header);
        tr.AddNewlyCreatedDBObject(header, true);

        for (int i = 0; i < config.Steps.Count; i++)
        {
            var step = config.Steps[i];
            bool isCurrent = i == meta.CurrentStep;
            bool isPast = i < meta.CurrentStep;
            string colorCode = isPast ? "\\C3;" : (isCurrent ? "\\C7;" : "\\C252;");

            MText stepText = new MText();
            stepText.Contents = $"{colorCode}Step {i}: {step.Label}";
            stepText.Location = new Point3d(startPt.X, currentY, 0);
            stepText.TextHeight = textHeightStep;
            stepText.Layer = InfoLayer;
            btr.AppendEntity(stepText);
            tr.AddNewlyCreatedDBObject(stepText, true);

            currentY -= textHeightStep * 1.5;

            for (int j = 0; j < step.Substeps.Count; j++)
            {
                bool isCurrentSub = (isCurrent && j == meta.CurrentSubstep);
                string prefix = isCurrentSub ? ">> " : "   ";

                MText subText = new MText();
                subText.ColorIndex = (int)(isCurrentSub ? 1 : 7);
                subText.Contents = $"{colorCode}{prefix}{step.Substeps[j].Label}";
                subText.Location = new Point3d(startPt.X + 1000, currentY, 0);
                subText.TextHeight = textHeightSub;
                subText.Layer = InfoLayer;
                btr.AppendEntity(subText);
                tr.AddNewlyCreatedDBObject(subText, true);

                currentY -= textHeightSub * 1.5;
            }
            currentY -= stepGap;
        }
    }

    public static void DrawDebugRow(Transaction tr, BlockTableRecord btr, ProjectDetailsDTO data, Point3d startPt)
    {
        double currentX = startPt.X;
        double yPos = startPt.Y;

        string pAttr = JsonConvert.SerializeObject(data.ProjectAttributes, Formatting.Indented);
        string config = JsonConvert.SerializeObject(data.AutodrawConfig, Formatting.Indented);

        currentX += DrawDebugBox(tr, btr, "project_attributes", pAttr, new Point3d(currentX, yPos, 0), 1);
        currentX += DrawDebugBox(tr, btr, "autodraw_config", config, new Point3d(currentX, yPos, 0), 3);
    }

    private static double DrawDebugBox(Transaction tr, BlockTableRecord btr, string title, string content, Point3d position, int colorIndex)
    {
        MText mtext = new MText();
        mtext.Contents = $"{{\\H400;\\C7;{title}}}\n\\P{content}";
        mtext.Location = new Point3d(position.X + 200, position.Y - 200, 0);
        mtext.TextHeight = 200.0;
        mtext.Width = 0.0;
        mtext.Layer = InfoLayer;

        btr.AppendEntity(mtext);
        tr.AddNewlyCreatedDBObject(mtext, true);

        Extents3d ext = mtext.GeometricExtents;
        double w = ext.MaxPoint.X - ext.MinPoint.X + 400;
        double h = ext.MaxPoint.Y - ext.MinPoint.Y + 400;

        DBPolyline box = new DBPolyline();
        box.AddVertexAt(0, new Point2d(position.X, position.Y), 0, 0, 0);
        box.AddVertexAt(1, new Point2d(position.X + w, position.Y), 0, 0, 0);
        box.AddVertexAt(2, new Point2d(position.X + w, position.Y - h), 0, 0, 0);
        box.AddVertexAt(3, new Point2d(position.X, position.Y - h), 0, 0, 0);
        box.Closed = true;
        box.ColorIndex = colorIndex;
        box.Layer = InfoLayer;

        btr.AppendEntity(box);
        tr.AddNewlyCreatedDBObject(box, true);

        return w;
    }
}
