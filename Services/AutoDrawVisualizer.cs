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
    /// Where the lineage's notes are laid out. Like INFO it is the plugin's own
    /// annotation, excluded from submission so the server never adopts it.
    /// </summary>
    public const string NotesLayer = "NOTES";

    /// <summary>
    /// The board sits at a fixed spot rather than tracking the drawing's edge,
    /// so it stays where the designer last looked for it instead of moving
    /// every time the geometry grows.
    /// </summary>
    private static readonly Point3d BoardPosition = new Point3d(-30000, 0, 0);

    /// <summary>
    /// Notes sit opposite the board, on the far side of the model. Fixed for
    /// the same reason the board is: it stays where it was last read.
    /// </summary>
    private static readonly Point3d NotesPosition = new Point3d(30000, 0, 0);


    /// <summary>Create the INFO layer if it is missing. Non-plotting: it is not part of the job.</summary>
    public static void EnsureInfoLayer(Transaction tr, Database db) => EnsureLayer(tr, db, InfoLayer);

    /// <summary>A non-plotting layer for the plugin's own annotation.</summary>
    public static void EnsureLayer(Transaction tr, Database db, string name)
    {
        LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) return;

        lt.UpgradeOpen();
        LayerTableRecord layer = new LayerTableRecord
        {
            Name = name,
            IsPlottable = false,
        };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
    }

    /// <summary>Erase the previous board so it can be redrawn from current state.</summary>
    public static void ClearInfoLayer(Transaction tr, Database db) => ClearLayer(tr, db, InfoLayer);

    /// <summary>Erase a layer's contents so it can be redrawn from current state.</summary>
    public static void ClearLayer(Transaction tr, Database db, string name)
    {
        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        var doomed = new List<ObjectId>();
        foreach (ObjectId id in ms)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent != null && ent.Layer == name) doomed.Add(id);
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

    /// <summary>Where the notes panel sits. Fixed, opposite the board.</summary>
    public static Point3d NotesOrigin()
    {
        return NotesPosition;
    }


    /// <summary>
    /// Lay out the lineage's notes beside the model, grouped by step.
    ///
    /// The server sends them already ordered and already filtered to the line
    /// now loaded, so switching branches changes what appears here. Nothing is
    /// stored in the drawing - it is redrawn from the record each time.
    /// </summary>
    public static void DrawNotes(Transaction tr, BlockTableRecord btr, List<NoteDTO> notes, Point3d startPt)
    {
        double currentY = startPt.Y;
        const double NoteWidth = 12000.0;

        MText header = new MText();
        header.Contents = "{\\H1200;\\C3;Notes}";
        header.Location = new Point3d(startPt.X, currentY + 2000, 0);
        header.TextHeight = 1200;
        header.Layer = NotesLayer;
        btr.AppendEntity(header);
        tr.AddNewlyCreatedDBObject(header, true);

        if (notes == null || notes.Count == 0)
        {
            MText empty = new MText();
            empty.Contents = "{\\C252;Nothing noted on this branch.}";
            empty.Location = new Point3d(startPt.X, currentY, 0);
            empty.TextHeight = 500;
            empty.Layer = NotesLayer;
            btr.AppendEntity(empty);
            tr.AddNewlyCreatedDBObject(empty, true);
            return;
        }

        string lastStep = null;
        foreach (NoteDTO note in notes)
        {
            if (note.StepLabel != lastStep)
            {
                MText stepText = new MText();
                stepText.Contents = "{\\C7;" + note.StepLabel + "}";
                stepText.Location = new Point3d(startPt.X, currentY, 0);
                stepText.TextHeight = 800;
                stepText.Layer = NotesLayer;
                btr.AppendEntity(stepText);
                tr.AddNewlyCreatedDBObject(stepText, true);
                currentY -= 1200;
                lastStep = note.StepLabel;
            }

            // The step's own account first, then anything a person added.
            // A note filed before its step has run is marked, so it is clear
            // nothing has been drawn for it yet.
            string text = "{\\C4;" + note.SubstepLabel + (note.Pending ? "  (not run yet)" : "") + "}";
            if (!string.IsNullOrWhiteSpace(note.Note))
            {
                // A step writes plain line breaks; MText wants paragraph marks.
                text += "\\P{\\C253;" + note.Note.Replace("\n", "\\P") + "}";
            }
            if (!string.IsNullOrWhiteSpace(note.UserNote))
            {
                text += "\\P{\\C2;note: " + note.UserNote + "}";
            }

            MText body = new MText();
            body.Contents = text;
            body.Location = new Point3d(startPt.X + 800, currentY, 0);
            body.TextHeight = 500;
            body.Width = NoteWidth;
            body.Layer = NotesLayer;
            btr.AppendEntity(body);
            tr.AddNewlyCreatedDBObject(body, true);

            // Two lines plus however many the note wrapped onto.
            currentY -= 900 + body.ActualHeight;
        }
    }

    public static void DrawStatusBoard(Transaction tr, BlockTableRecord btr, AutoDrawConfigDTO config, AutoDrawMetaDTO meta, Point3d startPt, string branch = null)
    {
        double currentY = startPt.Y;
        double stepGap = 1500;
        double textHeightStep = 800;
        double textHeightSub = 500;

        MText header = new MText();
        // Two branches draw the same geometry, so the heading has to say
        // which of them is in front of you.
        header.Contents = string.IsNullOrWhiteSpace(branch)
            ? "{\\H1200;\\C3;Steps}"
            : "{\\H1200;\\C3;Steps}  {\\H700;\\C7;branch: " + branch + "}";
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
