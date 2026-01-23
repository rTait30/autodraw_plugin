using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using autodraw_plugin.Models.Projects; 
using autodraw_plugin.Models.AutoDraw; 
using Newtonsoft.Json;
using System.Collections.Generic;

using DBPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace autodraw_plugin.Services;

public static class AutoDrawVisualizer
{
    public static void ClearAndPrepareLayer(Transaction tr, Database db)
    {
        // Placeholder: e.g. delete existing entities on specific layers
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
        btr.AppendEntity(header);
        tr.AddNewlyCreatedDBObject(header, true);

        for (int i = 0; i < config.steps.Count; i++)
        {
            var step = config.steps[i];
            bool isCurrent = i == meta.current_step;
            bool isPast = i < meta.current_step;
            string colorCode = isPast ? "\\C3;" : (isCurrent ? "\\C7;" : "\\C252;");

            MText stepText = new MText();
            stepText.Contents = $"{colorCode}Step {i}: {step.label}";
            stepText.Location = new Point3d(startPt.X, currentY, 0);
            stepText.TextHeight = textHeightStep;
            btr.AppendEntity(stepText);
            tr.AddNewlyCreatedDBObject(stepText, true);

            currentY -= textHeightStep * 1.5;

            for (int j = 0; j < step.substeps.Count; j++)
            {
                bool isCurrentSub = (isCurrent && j == meta.current_substep);
                string prefix = isCurrentSub ? ">> " : "   ";

                MText subText = new MText();
                subText.ColorIndex = (int)(isCurrentSub ? 1 : 7);
                subText.Contents = $"{colorCode}{prefix}{step.substeps[j].label}";
                subText.Location = new Point3d(startPt.X + 1000, currentY, 0);
                subText.TextHeight = textHeightSub;
                btr.AppendEntity(subText);
                tr.AddNewlyCreatedDBObject(subText, true);

                currentY -= textHeightSub * 1.5;
            }
            currentY -= stepGap;
        }
    }

    public static void DrawWorkflowBoxes(Transaction tr, BlockTableRecord btr, AutoDrawConfigDTO config, AutoDrawMetaDTO meta, AutoDrawRecordDTO record)
    {
        double boxSize = 20000;
        double gap = 0;
        double currentY = 0;

        for (int i = 0; i < config.steps.Count; i++)
        {
            var step = config.steps[i];
            Point3d stepOrigin = new Point3d(0, currentY, 0);
            bool isActiveStep = (i == meta.current_step);
            int colorIndex = isActiveStep ? 3 : 252; // Green or Gray

            DBPolyline box = new DBPolyline();
            box.AddVertexAt(0, new Point2d(0, currentY), 0, 0, 0);
            box.AddVertexAt(1, new Point2d(boxSize, currentY), 0, 0, 0);
            box.AddVertexAt(2, new Point2d(boxSize, currentY - boxSize), 0, 0, 0);
            box.AddVertexAt(3, new Point2d(0, currentY - boxSize), 0, 0, 0);
            box.Closed = true;
            box.ColorIndex = colorIndex;
            if (isActiveStep) box.ConstantWidth = 250;

            btr.AppendEntity(box);
            tr.AddNewlyCreatedDBObject(box, true);

            MText title = new MText();
            title.Contents = $"{{\\H500;Step {i}: {step.label}}}";
            title.Location = new Point3d(500, currentY - 500, 0);
            title.TextHeight = 500;
            title.ColorIndex = (int)(isActiveStep ? 1 : 7);
            btr.AppendEntity(title);
            tr.AddNewlyCreatedDBObject(title, true);

            if (record != null && record.geometry != null)
            {
                DrawStepGeometry(tr, btr, step, record.geometry, stepOrigin);
            }

            currentY -= (boxSize + gap);
        }
    }

    private static void DrawStepGeometry(Transaction tr, BlockTableRecord btr, ConfigStepDTO step, List<GeometryItemDTO> allGeometry, Point3d offset)
    {
        double drawOffsetX = 2000;
        double drawOffsetY = -2000;

        foreach (var item in allGeometry)
        {
            bool shouldDraw = false;
            foreach (var rule in step.show)
            {
                if (rule.query == "ad_layer" && item.ad_layer == rule.value)
                {
                    shouldDraw = true;
                    break;
                }
            }

            if (shouldDraw && item.type == "geo_line" && item.attributes != null)
            {
                var attr = item.attributes;
                if (attr.start != null && attr.end != null)
                {
                    Point3d start = new Point3d(attr.start[0], attr.start[1], 0).Add(new Vector3d(drawOffsetX, drawOffsetY, 0));
                    Point3d end = new Point3d(attr.end[0], attr.end[1], 0).Add(new Vector3d(drawOffsetX, drawOffsetY, 0));
                    
                    Line line = new Line(start, end);
                    // line.Layer = item.ad_layer; // Ensure layer exists first if used
                    line.TransformBy(Matrix3d.Displacement(offset.GetAsVector()));
                    
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                }
            }
        }
    }

    public static void DrawDebugRow(Transaction tr, BlockTableRecord btr, ProjectDetailsDTO data)
    {
        double currentX = -100000;
        double yPos = 0;
        double gap = 0;

        // Simplify debug for DTOs
        string pAttr = JsonConvert.SerializeObject(data.project_attributes, Formatting.Indented);
        string config = JsonConvert.SerializeObject(data.autodraw_config, Formatting.Indented);

        currentX += DrawDebugBox(tr, btr, "project_attributes", pAttr, new Point3d(currentX, yPos, 0), 1) + gap;
        currentX += DrawDebugBox(tr, btr, "autodraw_config", config, new Point3d(currentX, yPos, 0), 3) + gap;
    }

    private static double DrawDebugBox(Transaction tr, BlockTableRecord btr, string title, string content, Point3d position, int colorIndex)
    {
        MText mtext = new MText();
        mtext.Contents = $"{{\\H400;\\C7;{title}}}\n\\P{content}";
        mtext.Location = new Point3d(position.X + 200, position.Y - 200, 0);
        mtext.TextHeight = 200.0;
        mtext.Width = 0.0;

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

        btr.AppendEntity(box);
        tr.AddNewlyCreatedDBObject(box, true);

        return w;
    }
}
