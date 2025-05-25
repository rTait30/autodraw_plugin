using System;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using System.Text.RegularExpressions;



public class JsonListCommand : IExtensionApplication
{

    public void Initialize()
    {
        // Code that runs when AutoCAD loads your DLL
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage("\nautodraw plugin initialized.");
    }

    public void Terminate()
    {
        // Code that runs when AutoCAD shuts down
    }
    private class SurgicalConfig
    {
        public string Category { get; set; }
        public string Company { get; set; }
        public string Name { get; set; }
        public double Length { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Quantity { get; set; }
        public double fabricWidth { get; set; }
    }

    string url = "http://127.0.0.1:5001/copelands";

    [CommandMethod("ADSSC")]
    public async void ListAndDrawSurgicalConfigs()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        Database db = HostApplicationServices.WorkingDatabase;
        ed.WriteMessage("\nFetching available config files... ");

        try
        {
            using (HttpClient client = new HttpClient())
            {
                // Step 1: Fetch file list
                //string listUrl = "https://presentr.ai/copelands/list_configs/surgical";

                string listUrl = $"{url}/list_configs/surgical";
                string json = await client.GetStringAsync(listUrl);
                json = json.Trim('[', ']');
                string[] files = json.Split(',').Select(f => f.Trim(' ', '"')).ToArray();

                if (files.Length == 0)
                {
                    ed.WriteMessage("\nNo configuration files found.");
                    return;
                }

                for (int i = 0; i < files.Length; i++)
                    ed.WriteMessage($"\n  [{i}] {files[i]}");

                // Step 2: User selects file
                PromptIntegerOptions opts = new PromptIntegerOptions("\nEnter the number of the file to load:")
                {
                    LowerLimit = 0,
                    UpperLimit = files.Length - 1,
                    DefaultValue = 0
                };

                PromptIntegerResult result = ed.GetInteger(opts);
                if (result.Status != PromptStatus.OK) return;
                string selectedFile = files[result.Value];
                //string getUrl = $"https://presentr.ai/copelands/get_config/surgical/{selectedFile}";

                // Step 1: Remove ALL quote characters anywhere
                string cleanedFile = selectedFile.Replace("\"", "");

                // Step 2: Trim outer whitespace (spaces, newlines, tabs)
                cleanedFile = cleanedFile.Trim();

                // Step 3: Optionally remove brackets or anything you know shouldn’t be there
                cleanedFile = Regex.Replace(cleanedFile, @"[\[\]]", "");

                // Step 4: Build the final URL
                string getUrl = $"{url}/get_config/surgical/{cleanedFile}";

                ed.WriteMessage($"\nFetching config: {selectedFile}");
                string configJson = await client.GetStringAsync(getUrl);

                // Step 3: Parse config using Newtonsoft.Json
                var config = JsonConvert.DeserializeObject<SurgicalConfig>(configJson);
                if (config == null)
                {
                    ed.WriteMessage("\nFailed to parse config.");
                    return;
                }

                // Step 4: Prompt for fabric width
                double fabricHeight = PromptForFabricHeight(ed);
                if (fabricHeight <= 0) return;

                // Step 5: Draw fabric + panels
                Document doc = Application.DocumentManager.MdiActiveDocument;
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    BlockTableRecord btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                    double unitWidth = 2 * config.Height + config.Length;
                    double unitHeight = config.Width;
                    double totalWidth = config.Quantity * unitWidth;

                    // === Draw fabric background ===
                    Polyline fabric = new Polyline(4);
                    fabric.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                    fabric.AddVertexAt(1, new Point2d(totalWidth, 0), 0, 0, 0);
                    fabric.AddVertexAt(2, new Point2d(totalWidth, fabricHeight), 0, 0, 0);
                    fabric.AddVertexAt(3, new Point2d(0, fabricHeight), 0, 0, 0);
                    fabric.Closed = true;
                    fabric.ColorIndex = 8;  // gray outline
                    btr.AppendEntity(fabric);
                    tr.AddNewlyCreatedDBObject(fabric, true);

                    // === Draw each unit horizontally ===
                    for (int i = 0; i < config.Quantity; i++)
                    {
                        double baseX = i * unitWidth;
                        DrawFlatStrip(config, btr, tr, baseX, 0, i + 1);
                    }

                    tr.Commit();
                }

                ed.WriteMessage("\nFinished drawing surgical panels.");
            }
        }
        catch (HttpRequestException ex)
        {
            ed.WriteMessage($"\nHTTP Error: {ex.Message}");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }


    [CommandMethod("ADLSC")]
    public void LoadLocalSurgicalConfig()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        Database db = HostApplicationServices.WorkingDatabase;

        // Prompt user for project name
        PromptStringOptions pso = new PromptStringOptions("\nEnter project name:")
        {
            AllowSpaces = false
        };
        PromptResult pr = ed.GetString(pso);
        if (pr.Status != PromptStatus.OK) return;

        string projectName = pr.StringResult.Trim();
        string basePath = @"C:\AutoDraw";  // Adjust as needed
        string configPath = Path.Combine(basePath, projectName, projectName + ".json");

        if (!System.IO.File.Exists(configPath))
        {
            ed.WriteMessage($"\nConfig not found: {configPath}");
            return;
        }

        try
        {
            string configJson = System.IO.File.ReadAllText(configPath);
            var config = JsonConvert.DeserializeObject<SurgicalConfig>(configJson);
            if (config == null)
            {
                ed.WriteMessage("\nFailed to parse config.");
                return;
            }

            double fabricHeight = config.fabricWidth;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                double unitWidth = 2 * config.Height + config.Length;
                double unitHeight = config.Width;
                double totalWidth = config.Quantity * unitWidth;

                // Draw fabric background
                Polyline fabric = new Polyline(4);
                fabric.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                fabric.AddVertexAt(1, new Point2d(totalWidth, 0), 0, 0, 0);
                fabric.AddVertexAt(2, new Point2d(totalWidth, fabricHeight), 0, 0, 0);
                fabric.AddVertexAt(3, new Point2d(0, fabricHeight), 0, 0, 0);
                fabric.Closed = true;
                fabric.ColorIndex = 8;
                btr.AppendEntity(fabric);
                tr.AddNewlyCreatedDBObject(fabric, true);

                for (int i = 0; i < config.Quantity; i++)
                {
                    double baseX = i * unitWidth;
                    DrawFlatStrip(config, btr, tr, baseX, 0, i + 1);
                }

                tr.Commit();
            }

            ed.WriteMessage("\nFinished drawing surgical panels.");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    private double PromptForFabricHeight(Editor ed)
    {
        var opts = new PromptDoubleOptions("\nEnter fabric roll width (used as height in layout):")
        {
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = 5.0
        };

        var result = ed.GetDouble(opts);
        return result.Status == PromptStatus.OK ? result.Value : -1;
    }

    private void DrawFlatStrip(SurgicalConfig config, BlockTableRecord btr, Transaction tr, double baseX, double baseY, int index)
    {
        var panels = new[]
        {
            ("Back", config.Height),
            ("Top", config.Length),
            ("Front", config.Height)
        };

        double panelHeight = config.Width;
        double x = baseX;

        foreach (var (label, panelWidth) in panels)
        {
            Point2d pt = new Point2d(x, baseY);
            Polyline rect = new Polyline(4);
            rect.AddVertexAt(0, pt, 0, 0, 0);
            rect.AddVertexAt(1, new Point2d(pt.X + panelWidth, pt.Y), 0, 0, 0);
            rect.AddVertexAt(2, new Point2d(pt.X + panelWidth, pt.Y + panelHeight), 0, 0, 0);
            rect.AddVertexAt(3, new Point2d(pt.X, pt.Y + panelHeight), 0, 0, 0);
            rect.Closed = true;
            btr.AppendEntity(rect);
            tr.AddNewlyCreatedDBObject(rect, true);

            // Add label
            DBText text = new DBText
            {
                Position = new Point3d(pt.X + panelWidth / 2, pt.Y + panelHeight / 2, 0),
                Height = 0.25,
                TextString = $"{label} {index}",
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                AlignmentPoint = new Point3d(pt.X + panelWidth / 2, pt.Y + panelHeight / 2, 0),
                Justify = AttachmentPoint.MiddleCenter
            };
            btr.AppendEntity(text);
            tr.AddNewlyCreatedDBObject(text, true);

            // Draw dimensions if this is the first unit only
            if (index == 1)
                DrawDimensions(btr, tr, pt, panelWidth, panelHeight);

            x += panelWidth;
        }
    }

    private void DrawDimensions(BlockTableRecord btr, Transaction tr, Point2d pt, double width, double height)
    {
        // Bottom dimension
        AddDim(btr, tr, new Point3d(pt.X, pt.Y, 0), new Point3d(pt.X + width, pt.Y, 0), new Point3d(pt.X + width / 2, pt.Y - 0.5, 0));

        // Top dimension
        AddDim(btr, tr, new Point3d(pt.X, pt.Y + height, 0), new Point3d(pt.X + width, pt.Y + height, 0), new Point3d(pt.X + width / 2, pt.Y + height + 0.5, 0));

        // Left dimension
        AddDim(btr, tr, new Point3d(pt.X, pt.Y, 0), new Point3d(pt.X, pt.Y + height, 0), new Point3d(pt.X - 0.5, pt.Y + height / 2, 0));

        // Right dimension
        AddDim(btr, tr, new Point3d(pt.X + width, pt.Y, 0), new Point3d(pt.X + width, pt.Y + height, 0), new Point3d(pt.X + width + 0.5, pt.Y + height / 2, 0));
    }

    private void AddDim(BlockTableRecord btr, Transaction tr, Point3d start, Point3d end, Point3d linePos)
    {
        RotatedDimension dim = new RotatedDimension
        {
            XLine1Point = start,
            XLine2Point = end,
            DimLinePoint = linePos,
            DimensionStyle = btr.Database.Dimstyle,
            Rotation = (start.Y == end.Y) ? 0 : Math.PI / 2
        };
        btr.AppendEntity(dim);
        tr.AddNewlyCreatedDBObject(dim, true);
    }
}















public class UserPromptExample
{
    [CommandMethod("DrawShapeFromChoice")]
    public void DrawShapeFromChoice()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        // Ask user to choose 1, 2, or 3
        PromptStringOptions prompt = new PromptStringOptions("\nPersonalTest Choose a shape (1 = Line, 2 = Circle, 3 = Rectangle):");
        prompt.AllowSpaces = false;
        var result = ed.GetString(prompt);

        if (result.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nInvalid input.");
            return;
        }

        string choice = result.StringResult;

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            switch (choice)
            {
                case "1":
                    Line line = new Line(new Point3d(0, 0, 0), new Point3d(1000, 1000, 0));
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                    ed.WriteMessage("\nDrew a line. COPELANDS");
                    break;

                case "2":
                    Circle circle = new Circle(new Point3d(0, 0, 0), Vector3d.ZAxis, 500);
                    btr.AppendEntity(circle);
                    tr.AddNewlyCreatedDBObject(circle, true);
                    ed.WriteMessage("\nDrew a circle.");
                    break;

                case "3":
                    Polyline rect = new Polyline();
                    rect.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                    rect.AddVertexAt(1, new Point2d(0, 1000), 0, 0, 0);
                    rect.AddVertexAt(2, new Point2d(2000, 1000), 0, 0, 0);
                    rect.AddVertexAt(3, new Point2d(2000, 0), 0, 0, 0);
                    rect.Closed = true;
                    btr.AppendEntity(rect);
                    tr.AddNewlyCreatedDBObject(rect, true);
                    ed.WriteMessage("\nDrew a rectangle.");
                    break;

                default:
                    ed.WriteMessage("\nInvalid choice. Please enter 1, 2, or 3.");
                    break;
            }

            tr.Commit();
        }
    }
}