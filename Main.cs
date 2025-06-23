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
        ed.WriteMessage("\nFetching available cover projects... ");

        try
        {
            using (HttpClient client = new HttpClient())
            {
                var covers = await FetchLatestCoverProjects(client, ed);
                if (covers == null || covers.Count == 0) return;

                // Prompt user for project id
                PromptStringOptions prompt = new PromptStringOptions("\nEnter the id of the project to draw (or press ESC to cancel):");
                prompt.AllowSpaces = false;
                PromptResult result = ed.GetString(prompt);

                if (result.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\nCancelled.");
                    return;
                }

                string selectedId = result.StringResult.Trim();
                var selectedProject = covers.FirstOrDefault(p => p.Id == selectedId);

                if (selectedProject == null)
                {
                    ed.WriteMessage($"\nNo project with id '{selectedId}' found in the list.");
                    return;
                }

                ed.WriteMessage($"\nDrawing project: {selectedProject.Name} (ID: {selectedProject.Id}) (Client:{selectedProject.Client}) ");

                // Fetch full project detail (attributes + calculated)
                var detail = await FetchFullProjectDetail(client, selectedId, ed);
                if (detail == null) return;

                double? fabricWidth = detail.Attributes?.FabricWidth;
                bool hasNest = detail.Calculated?.NestData != null;

                double userFabricWidth = 0;

                if (fabricWidth.HasValue)
                {
                    // Custom prompt: Yes/Calculate with width
                    PromptKeywordOptions pko = new PromptKeywordOptions(
                        $"\nPrecalculated nest found using Fabric Width: {fabricWidth.Value}. Use this nest?")
                    {
                        AllowNone = false
                    };
                    pko.Keywords.Add("Yes");
                    pko.Keywords.Add("Calculate with width");
                    pko.Keywords.Default = "Yes";
                    var nestResult = ed.GetKeywords(pko);

                    if (nestResult.Status != PromptStatus.OK)
                    {
                        ed.WriteMessage("\nCancelled.");
                        return;
                    }

                    if (nestResult.StringResult == "Yes")
                    {
                        ed.WriteMessage("\nUsing precalculated nest.");
                        if (hasNest)
                        {
                            PrintNestInfo(detail.Calculated.NestData, ed);
                        }
                    }
                    else // "Calculate with width"
                    {
                        double newWidth = PromptForFabricWidth(ed, fabricWidth.Value);
                        ed.WriteMessage($"\nRecalculating with fabric width: {newWidth}");
                        // (future: recalc logic here)
                    }
                }
                else
                {
                    double newWidth = PromptForFabricWidth(ed, null);
                    ed.WriteMessage($"\nRecalculating with fabric width: {newWidth}");
                    // (future: recalc logic here)
                }

                // For now, do not use fabricWidth or nestData in drawing
                var attributes = detail.Attributes;
                if (attributes == null) return;
                //var attributes = detail.Attributes;
                var nestData = detail.Calculated?.NestData;
               // double? fabricWidth = detail.Attributes?.FabricWidth ?? detail.Calculated?.FabricWidth;
                if (attributes == null || nestData == null || !fabricWidth.HasValue) return;
                DrawCoverRectangle(attributes, nestData, fabricWidth.Value);
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

    // Helper to fetch full project detail (attributes + calculated)
    private async Task<FullProjectDetail> FetchFullProjectDetail(HttpClient client, string projectId, Editor ed)
    {
        string detailUrl = $"{url}/api/project/{projectId}";
        string detailJson = await client.GetStringAsync(detailUrl);
        var detail = JsonConvert.DeserializeObject<FullProjectDetail>(detailJson);

        if (detail?.Attributes == null)
        {
            ed.WriteMessage("\nFailed to get project attributes.");
            return null;
        }
        return detail;
    }

    // Prompt for fabric width, optionally showing a default
    private double PromptForFabricWidth(Editor ed, double? defaultValue)
    {
        var opts = new PromptDoubleOptions("\nEnter fabric roll width (used as height in layout):")
        {
            AllowNegative = false,
            AllowZero = false,
            DefaultValue = defaultValue ?? 1000
        };
        var result = ed.GetDouble(opts);
        return result.Status == PromptStatus.OK ? result.Value : (defaultValue ?? 1000);
    }

    // Print basic info about nestData
    private void PrintNestInfo(NestData nest, Editor ed)
    {
        if (nest == null)
        {
            ed.WriteMessage("\nNo nest data available.");
            return;
        }

        ed.WriteMessage($"\nNest Info:");
        ed.WriteMessage($"\n  Total width: {nest.TotalWidth}");
        ed.WriteMessage($"\n  Used bin width: {nest.UsedBinWidth}");
        if (nest.Panels != null)
        {
            ed.WriteMessage($"\n  Panels:");
            foreach (var panel in nest.Panels)
            {
                ed.WriteMessage($"\n    {panel.Key}: x={panel.Value.X}, y={panel.Value.Y}, rotated={panel.Value.Rotated}");
            }
        }
    }

    // --- Data classes for deserialization ---
    private class FullProjectDetail
    {
        [JsonProperty("attributes")]
        public ProjectAttributes Attributes { get; set; }

        [JsonProperty("calculated")]
        public CalculatedData Calculated { get; set; }
    }

    private class CalculatedData
    {
        [JsonProperty("fabricWidth")]
        public double? FabricWidth { get; set; }

        [JsonProperty("nestData")]
        public NestData NestData { get; set; }
    }

    private class NestData
    {
        [JsonProperty("panels")]
        public Dictionary<string, NestPanel> Panels { get; set; }

        [JsonProperty("total_width")]
        public double TotalWidth { get; set; }

        [JsonProperty("used_bin_width")]
        public double UsedBinWidth { get; set; }
    }

    private class NestPanel
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }

        [JsonProperty("rotated")]
        public bool Rotated { get; set; }
    }

    // Extend ProjectAttributes as needed
    private class ProjectAttributes
    {
        [JsonProperty("width")]
        public double Width { get; set; }

        [JsonProperty("length")]
        public double Length { get; set; }

        [JsonProperty("height")]
        public double Height { get; set; }

        [JsonProperty("seam")]
        public double Seam { get; set; }

        [JsonProperty("fabricWidth")]
        public double? FabricWidth { get; set; }
    }


    // --- Abstractions ---

    private async Task<List<ProjectInfo>> FetchLatestCoverProjects(HttpClient client, Editor ed)
    {
        string listUrl = $"{url}/api/projects/list";
        string json = await client.GetStringAsync(listUrl);
        var projects = JsonConvert.DeserializeObject<List<ProjectInfo>>(json);

        if (projects == null)
        {
            ed.WriteMessage("\nNo projects found or failed to parse response.");
            return null;
        }

        var covers = projects
            .Where(p => p.Type == "cover")
            .OrderBy(p => p.UpdatedAt)
            .TakeLast(5)
            .ToList();

        if (covers.Count == 0)
        {
            ed.WriteMessage("\nNo cover projects found.");
            return null;
        }

        ed.WriteMessage($"\nLatest {covers.Count} cover projects:");
        foreach (var cover in covers)
        {
            ed.WriteMessage($"\n{cover.Id}: {cover.Name}");
        }

        return covers;
    }

    private async Task<ProjectAttributes> FetchProjectAttributes(HttpClient client, string projectId, Editor ed)
    {
        string detailUrl = $"{url}/api/project/{projectId}";
        string detailJson = await client.GetStringAsync(detailUrl);
        var detail = JsonConvert.DeserializeObject<ProjectDetail>(detailJson);

        if (detail?.Attributes == null)
        {
            ed.WriteMessage("\nFailed to get project attributes.");
            return null;
        }

        ed.WriteMessage($"\nWidth: {detail.Attributes.Width}");
        ed.WriteMessage($"\nLength: {detail.Attributes.Length}");
        ed.WriteMessage($"\nHeight: {detail.Attributes.Height}");

        return detail.Attributes;
    }

    // This struct represents a rectangle to draw (could be extended for more shapes)
    private struct RectLayout
    {
        public double X, Y, Width, Height;
        public string Label;
    }

    private void DrawCoverRectangle(ProjectAttributes attr, NestData nestData, double fabricWidth)
    {
        double height = attr.Width + attr.Seam * 2;
        double width = attr.Height * 2 + attr.Length;
        double margin = 50; // Margin between panels

        double fold1X = attr.Height;
        double fold2X = attr.Height + attr.Length;

        double sidePanelWidth = attr.Height;
        double sidePanelHeight = attr.Length;

        Database db = HostApplicationServices.WorkingDatabase;
        Document doc = Application.DocumentManager.MdiActiveDocument;
        using (DocumentLock docLock = doc.LockDocument())
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // --- Draw nest box (fabric area) below the panels ---
            if (nestData != null && fabricWidth > 0)
            {
                double nestBoxY = - fabricWidth - margin;
                Polyline fabric = new Polyline(4);
                fabric.AddVertexAt(0, new Point2d(0, nestBoxY), 0, 0, 0);
                fabric.AddVertexAt(1, new Point2d(nestData.TotalWidth, nestBoxY), 0, 0, 0);
                fabric.AddVertexAt(2, new Point2d(nestData.TotalWidth, nestBoxY + fabricWidth), 0, 0, 0);
                fabric.AddVertexAt(3, new Point2d(0, nestBoxY + fabricWidth), 0, 0, 0);
                fabric.Closed = true;
                fabric.ColorIndex = 8; // gray outline
                btr.AppendEntity(fabric);
                tr.AddNewlyCreatedDBObject(fabric, true);
            }

            // Draw main rectangle
            Line bottom = new Line(new Point3d(0, 0, 0), new Point3d(width, 0, 0));
            Line right = new Line(new Point3d(width, 0, 0), new Point3d(width, height, 0));
            Line top = new Line(new Point3d(width, height, 0), new Point3d(0, height, 0));
            Line left = new Line(new Point3d(0, height, 0), new Point3d(0, 0, 0));
            btr.AppendEntity(bottom); tr.AddNewlyCreatedDBObject(bottom, true);
            btr.AppendEntity(right); tr.AddNewlyCreatedDBObject(right, true);
            btr.AppendEntity(top); tr.AddNewlyCreatedDBObject(top, true);
            btr.AppendEntity(left); tr.AddNewlyCreatedDBObject(left, true);

            // Ensure linetype exists
            EnsureLinetype(db, "DASHED");

            // Draw fold lines (red, dashed)
            Line fold1 = new Line(new Point3d(fold1X, 0, 0), new Point3d(fold1X, height, 0));
            Line fold2 = new Line(new Point3d(fold2X, 0, 0), new Point3d(fold2X, height, 0));
            fold1.Linetype = "DASHED";
            fold2.Linetype = "DASHED";
            fold1.ColorIndex = 1; // Red
            fold2.ColorIndex = 1; // Red
            btr.AppendEntity(fold1); tr.AddNewlyCreatedDBObject(fold1, true);
            btr.AppendEntity(fold2); tr.AddNewlyCreatedDBObject(fold2, true);

            AddFoldLabel(btr, tr, fold1X, height, "Fold");
            AddFoldLabel(btr, tr, fold2X, height, "Fold");

            // Draw seam lines (top and bottom, blue)
            Line seamTop = new Line(new Point3d(0, height - attr.Seam, 0), new Point3d(width, height - attr.Seam, 0));
            Line seamBottom = new Line(new Point3d(0, attr.Seam, 0), new Point3d(width, attr.Seam, 0));
            seamTop.ColorIndex = 5; // Blue
            seamBottom.ColorIndex = 5; // Blue
            btr.AppendEntity(seamTop); tr.AddNewlyCreatedDBObject(seamTop, true);
            btr.AppendEntity(seamBottom); tr.AddNewlyCreatedDBObject(seamBottom, true);

            // Draw side panels to the right of the main panel, with margin
            for (int i = 0; i < 2; i++)
            {
                double baseX = width + margin + i * (sidePanelWidth + margin);
                // Rectangle for side panel
                Line sBottom = new Line(new Point3d(baseX, 0, 0), new Point3d(baseX + sidePanelWidth, 0, 0));
                Line sRight = new Line(new Point3d(baseX + sidePanelWidth, 0, 0), new Point3d(baseX + sidePanelWidth, sidePanelHeight, 0));
                Line sTop = new Line(new Point3d(baseX + sidePanelWidth, sidePanelHeight, 0), new Point3d(baseX, sidePanelHeight, 0));
                Line sLeft = new Line(new Point3d(baseX, sidePanelHeight, 0), new Point3d(baseX, 0, 0));
                btr.AppendEntity(sBottom); tr.AddNewlyCreatedDBObject(sBottom, true);
                btr.AppendEntity(sRight); tr.AddNewlyCreatedDBObject(sRight, true);
                btr.AppendEntity(sTop); tr.AddNewlyCreatedDBObject(sTop, true);
                btr.AppendEntity(sLeft); tr.AddNewlyCreatedDBObject(sLeft, true);

                // Add label to each side panel
                AddFoldLabel(btr, tr, baseX + sidePanelWidth / 2, sidePanelHeight, $"Side {i + 1}");
            }

            tr.Commit();
        }
    }


    private void EnsureLinetype(Database db, string linetypeName)
    {
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            LinetypeTable ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!ltt.Has(linetypeName))
            {
                db.LoadLineTypeFile(linetypeName, "acad.lin");
            }
            tr.Commit();
        }
    }

    private void AddFoldLabel(BlockTableRecord btr, Transaction tr, double x, double height, string label)
    {
        DBText text = new DBText
        {
            Position = new Point3d(x, height + 50, 0),
            Height = 50,
            TextString = label,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextBottom,
            AlignmentPoint = new Point3d(x, height + 50, 0),
            Justify = AttachmentPoint.BottomCenter
        };
        btr.AppendEntity(text);
        tr.AddNewlyCreatedDBObject(text, true);
    }


    // Helper class for project details
    private class ProjectDetail
    {
        [JsonProperty("attributes")]
        public ProjectAttributes Attributes { get; set; }
    }

    // Helper class for deserialization
    private class ProjectInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("client")]
        public string Client { get; set; }

        [JsonProperty("updated_at")]
        public DateTime UpdatedAt { get; set; }
    }



    [CommandMethod("ADSSC_OLD")]
    public async void ListAndDrawSurgicalConfigsOLD()
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