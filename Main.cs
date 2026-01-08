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
using System.Text;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;

using Exception = System.Exception;

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


   
    [CommandMethod("InspectMPanelPolylines")]
    public void InspectMPanelPolylines()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        // Prompt user to select polylines on mPanel layer
        PromptSelectionOptions opts = new PromptSelectionOptions();
        opts.MessageForAdding = "\nSelect mPanel polylines (on layer 'mPanel'):";

        // Filter for LWPOLYLINEs on 'mPanel' layer
        TypedValue[] filterValues = new TypedValue[]
        {
            new TypedValue((int)DxfCode.LayerName, "mPanel"),
            new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
        };
        SelectionFilter filter = new SelectionFilter(filterValues);

        PromptSelectionResult selResult = ed.GetSelection(opts, filter);
        if (selResult.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo valid polylines selected.");
            return;
        }

        SelectionSet ss = selResult.Value;

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            int count = 0;

            foreach (ObjectId id in ss.GetObjectIds())
            {
                Polyline pline = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pline == null) continue;

                count++;

                ed.WriteMessage($"\n--- Polyline #{count} ---");
                ed.WriteMessage($"\nHandle: {pline.Handle}");
                ed.WriteMessage($"\nClosed: {pline.Closed}");
                ed.WriteMessage($"\nNumber of Vertices: {pline.NumberOfVertices}");

                // Bounding box
                Extents3d ext = pline.GeometricExtents;
                ed.WriteMessage($"\nBounds: Min({ext.MinPoint.X:0.##}, {ext.MinPoint.Y:0.##}), Max({ext.MaxPoint.X:0.##}, {ext.MaxPoint.Y:0.##})");

                // Vertices
                for (int i = 0; i < pline.NumberOfVertices; i++)
                {
                    var pt = pline.GetPoint2dAt(i);
                    ed.WriteMessage($"\n  Vertex {i + 1}: ({pt.X:0.##}, {pt.Y:0.##})");
                }

                ed.WriteMessage("\n");
            }

            tr.Commit();
        }

        ed.WriteMessage($"\nTotal polylines inspected: {ss.Count}");
    }

    private readonly string baseDirectory = @"C:\AutoDraw\";

    class ProjectData
    {
        public string name { get; set; }
        public string type { get; set; }
        public Attributes attributes { get; set; }
    }

    class Attributes
    {
        public int pointCount { get; set; }
        public Dictionary<string, double> dimensions { get; set; }
        public Dictionary<string, PointAttributes> points { get; set; }
        public string exitPoint { get; set; }
    }

    class PointAttributes
    {
        public string fixingType { get; set; }
        public double height { get; set; }
        public double tensionAllowance { get; set; }
    }

    [CommandMethod("ADSS")]
    public void RunADSS()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;

        if (!Directory.Exists(baseDirectory))
        {
            ed.WriteMessage($"\nProject directory not found: {baseDirectory}");
            return;
        }

        var projectFolders = Directory.GetDirectories(baseDirectory)
            .Where(dir => File.Exists(Path.Combine(dir, Path.GetFileName(dir) + ".json")))
            .ToList();

        if (projectFolders.Count == 0)
        {
            ed.WriteMessage("\nNo valid project folders with matching JSON files found.");
            return;
        }

        ed.WriteMessage("\nAvailable project folders:\n");
        for (int i = 0; i < projectFolders.Count; i++)
        {
            string name = Path.GetFileName(projectFolders[i]);
            ed.WriteMessage($"{i + 1}. {name}\n");
        }

        PromptIntegerOptions opt = new PromptIntegerOptions("\nSelect project number:")
        {
            LowerLimit = 1,
            UpperLimit = projectFolders.Count
        };

        PromptIntegerResult res = ed.GetInteger(opt);
        if (res.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nCancelled.");
            return;
        }

        string selectedFolder = projectFolders[res.Value - 1];
        string projectName = Path.GetFileName(selectedFolder);
        string jsonPath = Path.Combine(selectedFolder, projectName + ".json");

        if (!File.Exists(jsonPath))
        {
            ed.WriteMessage($"\nError: JSON not found: {jsonPath}");
            return;
        }

        // Load JSON
        ProjectData data;
        try
        {
            string jsonText = File.ReadAllText(jsonPath);
            data = JsonConvert.DeserializeObject<ProjectData>(jsonText);
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nFailed to parse JSON: {ex.Message}");
            return;
        }

        ed.WriteMessage($"\nLoaded project: {data.name}");
        ed.WriteMessage($"\nPoint count: {data.attributes.pointCount}");
        ed.WriteMessage($"\nPoint labels: {string.Join(", ", data.attributes.points.Keys.OrderBy(k => k))}");

        ed.WriteMessage("\nDimensions:");
        foreach (var kv in data.attributes.dimensions.OrderBy(kvp => kvp.Key))
        {
            ed.WriteMessage($"\n  {kv.Key}: {kv.Value} mm");
        }
        ed.WriteMessage($"\nExit point: {data.attributes.exitPoint}");

        // (Optional: save this folder path for storing DWG later)
        /*
        doc.UserData = new Dictionary<string, object>
        {
            { "ProjectFolder", selectedFolder }
        };
        */

        ed.WriteMessage("\nReady to proceed with drawing matching.");

        ed.WriteMessage("\nPlease select patern.");


    }

    [CommandMethod("SumSelectedLineLengths")]
    public void SumSelectedLineLengths()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        // Prompt user to select lines
        PromptSelectionOptions opts = new PromptSelectionOptions
        {
            MessageForAdding = "\nSelect lines to sum their lengths:"
        };

        // Filter for LINE entities only
        TypedValue[] filterValues = new TypedValue[]
        {
        new TypedValue((int)DxfCode.Start, "LINE")
        };
        SelectionFilter filter = new SelectionFilter(filterValues);

        PromptSelectionResult selResult = ed.GetSelection(opts, filter);
        if (selResult.Status != PromptStatus.OK)
        {
            ed.WriteMessage("\nNo lines selected.");
            return;
        }

        SelectionSet ss = selResult.Value;
        double totalLength = 0.0;

        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in ss.GetObjectIds())
            {
                Line line = tr.GetObject(id, OpenMode.ForRead) as Line;
                if (line != null)
                {
                    totalLength += line.Length;
                }
            }
            tr.Commit();
        }

        ed.WriteMessage($"\nTotal length of selected lines: {totalLength:0.##}");
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


// Explicit alias to avoid ambiguity


namespace ProductDesignPlugin
{
    // --- 1. GENERIC DATA MODELS ---
    public class LoginRequest
    {
        public string username { get; set; }
        public string password { get; set; }
    }

    public class LoginResponse
    {
        public string access_token { get; set; }
        public string role { get; set; }
        public string username { get; set; }
        public bool verified { get; set; }
    }

    public class GeometryRequest
    {
        public string doc_id { get; set; }     // e.g., "geometry_json"
        public string project_id { get; set; } // e.g., "123"
    }

    // --- 2. SESSION STATE ---
    public static class PluginSession
    {
        public static string AuthToken { get; private set; }
        public static string CurrentUser { get; private set; }

        // TODO: Update to your real API URL
        public static string BaseUrl { get; } = "http://localhost:5173/copelands/api";

        public static void SetSession(string token, string user)
        {
            AuthToken = token;
            CurrentUser = user;
        }

        public static bool IsLoggedIn => !string.IsNullOrEmpty(AuthToken);
    }

    // --- 3. AUTH COMMANDS (Generic) ---
    public class AuthCommands
    {
        private static readonly HttpClient client = new HttpClient();

        [CommandMethod("AD_LOGIN")] // Renamed from SHADE_LOGIN
        public async void LoginCmd()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            try
            {
                PromptStringOptions userOpts = new PromptStringOptions("\nEnter Username: ");
                userOpts.AllowSpaces = false;
                PromptResult userRes = ed.GetString(userOpts);
                if (userRes.Status != PromptStatus.OK) return;

                PromptStringOptions passOpts = new PromptStringOptions("\nEnter Password: ");
                passOpts.AllowSpaces = true;
                PromptResult passRes = ed.GetString(passOpts);
                if (passRes.Status != PromptStatus.OK) return;

                ed.WriteMessage("\nConnecting...");

                var loginData = new LoginRequest
                {
                    username = userRes.StringResult,
                    password = passRes.StringResult
                };

                string json = JsonConvert.SerializeObject(loginData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                string url = $"{PluginSession.BaseUrl}/login";
                HttpResponseMessage response = await client.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    string responseString = await response.Content.ReadAsStringAsync();
                    LoginResponse data = JsonConvert.DeserializeObject<LoginResponse>(responseString);

                    PluginSession.SetSession(data.access_token, data.username);

                    ed.WriteMessage($"\n--- SUCCESS: Logged in as {data.username} ---");
                }
                else
                {
                    ed.WriteMessage($"\nLogin Failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nError: {ex.Message}");
            }
        }

        [CommandMethod("AD_STATUS")]
        public void CheckStatus()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            if (PluginSession.IsLoggedIn)
                ed.WriteMessage($"\nLogged in as: {PluginSession.CurrentUser}");
            else
                ed.WriteMessage("\nNot logged in.");
        }
    }

    // --- 4. GEOMETRY COMMANDS (Generic) ---
    public class GeometryCommands
    {
        private static readonly HttpClient client = new HttpClient();

        [CommandMethod("AD_IMPORT_GEO")] // Renamed from SHADE_GET_GEO
        public async void ImportGeometryCmd()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            if (!PluginSession.IsLoggedIn)
            {
                ed.WriteMessage("\nPlease login first (AD_LOGIN).");
                return;
            }

            try
            {
                // A. GET PROJECT ID
                PromptStringOptions idOpts = new PromptStringOptions("\nEnter Project ID: ");
                idOpts.AllowSpaces = false;
                PromptResult idRes = ed.GetString(idOpts);
                if (idRes.Status != PromptStatus.OK) return;

                // B. GET OFFSET (With Default)
                Point3d offsetVector = new Point3d(0, 0, 0); // Default

                PromptStringOptions offOpts = new PromptStringOptions($"\nEnter Offset X,Y (Default: 0,0,0): ");
                offOpts.AllowSpaces = false;
                offOpts.UseDefaultValue = true; // Allows hitting Enter

                PromptResult offRes = ed.GetString(offOpts);

                if (offRes.Status == PromptStatus.OK && !string.IsNullOrEmpty(offRes.StringResult))
                {
                    // Parse "X,Y" or "X,Y,Z"
                    string[] parts = offRes.StringResult.Split(',');
                    double x = parts.Length > 0 ? double.Parse(parts[0]) : 0;
                    double y = parts.Length > 1 ? double.Parse(parts[1]) : 0;
                    double z = parts.Length > 2 ? double.Parse(parts[2]) : 0;
                    offsetVector = new Point3d(x, y, z);
                }

                // C. FETCH DATA
                var requestData = new GeometryRequest
                {
                    doc_id = "geometry_json",
                    project_id = idRes.StringResult
                };

                string jsonBody = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PluginSession.AuthToken);
                string url = $"{PluginSession.BaseUrl}/project/generate_document";

                ed.WriteMessage("\nFetching geometry...");
                HttpResponseMessage response = await client.PostAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    JArray geometryData = JArray.Parse(jsonResponse);

                    // PASS THE OFFSET TO THE DRAW FUNCTION
                    DrawFromJSON(doc, geometryData, offsetVector);
                }
                else
                {
                    ed.WriteMessage($"\nRequest Failed: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nError: {ex.Message}");
            }
        }

        private void DrawFromJSON(Document doc, JArray data, Point3d offset)
        {
            Database db = doc.Database;
            Editor ed = doc.Editor;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                int count = 0;
                double? minY = null, maxY = null; // For bounding box

                // --- 1. Draw original geometry ---
                foreach (JObject item in data)
                {
                    try
                    {
                        string type = item["type"]?.ToString().ToLower();
                        Entity ent = null;

                        string layerName = item["dxfattribs"]?["layer"]?.ToString() ?? "0";
                        EnsureLayerExists(tr, db, layerName);

                        switch (type)
                        {
                            case "mtext":
                                MText mtext = new MText();
                                mtext.Contents = item["text"]?.ToString();
                                mtext.Location = ParsePoint(item["location"]).Add(offset.GetAsVector());
                                string heightStr = item["dxfattribs"]?["char_height"]?.ToString();
                                if (double.TryParse(heightStr, out double h)) mtext.TextHeight = h;
                                ent = mtext;
                                break;

                            case "point":
                                DBPoint point = new DBPoint();
                                point.Position = ParsePoint(item["location"]).Add(offset.GetAsVector());
                                ent = point;
                                break;

                            case "circle":
                                Circle circ = new Circle();
                                circ.Center = ParsePoint(item["center"]).Add(offset.GetAsVector());
                                circ.Radius = (double)item["radius"];
                                ent = circ;
                                break;

                            case "line":
                                Line line = new Line();
                                line.StartPoint = ParsePoint(item["start"]).Add(offset.GetAsVector());
                                line.EndPoint = ParsePoint(item["end"]).Add(offset.GetAsVector());
                                ent = line;
                                break;
                        }

                        if (ent != null)
                        {
                            ent.Layer = layerName;
                            btr.AppendEntity(ent);
                            tr.AddNewlyCreatedDBObject(ent, true);
                            count++;

                            // Track Y bounds for all entities
                            Extents3d? ext = null;
                            try { ext = ent.GeometricExtents; } catch { }
                            if (ext.HasValue)
                            {
                                double y1 = ext.Value.MinPoint.Y;
                                double y2 = ext.Value.MaxPoint.Y;
                                minY = minY.HasValue ? Math.Min(minY.Value, y1) : y1;
                                maxY = maxY.HasValue ? Math.Max(maxY.Value, y2) : y2;
                            }
                        }
                    }
                    catch (Exception innerEx)
                    {
                        ed.WriteMessage($"\nSkipped item: {innerEx.Message}");
                    }
                }

                // --- 2. Draw all lines on AD_WORK_LINE, just below, and a line between the 2 highest workpoints ---
                if (minY.HasValue && maxY.HasValue)
                {
                    double padding = 200.0; // You can adjust this value
                    double yOffset = minY.Value - (maxY.Value - minY.Value) - padding;

                    EnsureLayerExists(tr, db, "AD_WORK_LINE");

                    // 1. Draw all lines on AD_WORK_LINE
                    foreach (JObject item in data)
                    {
                        string origLayer = item["dxfattribs"]?["layer"]?.ToString() ?? "";
                        if (!origLayer.Equals("AD_WORK_LINE", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string type = item["type"]?.ToString().ToLower();
                        if (type != "line") continue;

                        // Parse and offset line
                        Point3d start = ParsePoint(item["start"]).Add(offset.GetAsVector());
                        Point3d end = ParsePoint(item["end"]).Add(offset.GetAsVector());

                        // Move down by (maxY - minY + padding)
                        Vector3d down = new Vector3d(0, yOffset - minY.Value, 0);
                        Line workLine = new Line(start.Add(down), end.Add(down));
                        workLine.Layer = "AD_WORK_LINE";
                        btr.AppendEntity(workLine);
                        tr.AddNewlyCreatedDBObject(workLine, true);
                    }



                }


                tr.Commit();
                ed.WriteMessage($"\n--- SUCCESS: Imported {count} objects with Offset ({offset.X}, {offset.Y}) ---");
                ed.WriteMessage($"\n--- Also drew lines on AD_WORK_LINE below original drawing. ---");
            }
        }


        private Point3d ParsePoint(JToken token)
        {
            if (token == null) return new Point3d(0, 0, 0);
            double[] coords = token.ToObject<double[]>();
            double x = coords.Length > 0 ? coords[0] : 0;
            double y = coords.Length > 1 ? coords[1] : 0;
            double z = coords.Length > 2 ? coords[2] : 0;
            return new Point3d(x, y, z);
        }

        private void EnsureLayerExists(Transaction tr, Database db, string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return;
            LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                LayerTableRecord ltr = new LayerTableRecord();
                ltr.Name = layerName;
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
                lt.DowngradeOpen();
            }
        }
    }

    public class ProjectListCommands
    {
        [CommandMethod("AD_LIST")]
        public async void ListProjectsInDesign()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db = doc.Database;
            string endpoint = $"{PluginSession.BaseUrl}/projects/list";

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    // Use the authorization token if available
                    if (!string.IsNullOrEmpty(PluginSession.AuthToken))
                    {
                        client.DefaultRequestHeaders.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", PluginSession.AuthToken);
                    }

                    string json = await client.GetStringAsync(endpoint);
                    JArray projects = JArray.Parse(json);

                    // Filter for status == "in_design"
                    var inDesign = projects
                        .Where(p => (string)p["status"] == "in_design")
                        .ToList();

                    if (inDesign.Count == 0)
                    {
                        ed.WriteMessage("\nNo projects with status 'in_design' found.");
                        return;
                    }

                    // Print table to command line
                    string header = string.Format(
                        "| {0,4} | {1,-30} | {2,-25} | {3,-12} | {4,-20} | {5,-15} | {6,-10} |",
                        "ID", "Name", "Client", "Type", "Due Date", "Status", "Info");
                    string sep = new string('-', header.Length);

                    ed.WriteMessage($"\n{sep}\n{header}\n{sep}");
                    foreach (var p in inDesign)
                    {
                        ed.WriteMessage(
                            $"\n| {p["id"],4} | {p["name"],-30} | {p["client"],-25} | {p["type"],-12} | {p["due_date"],-20} | {p["status"],-15} | {p["info"],-10} |");
                    }
                    ed.WriteMessage($"\n{sep}\n");

                    // Draw table in AutoCAD
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                        int rows = inDesign.Count + 1; // +1 for header
                        int cols = 7;
                        double totalWidth = 10000.0;
                        double colWidth = totalWidth / cols;
                        double rowHeight = 200.0; // Reasonable for scaled text

                        Table table = new Table();
                        table.TableStyle = db.Tablestyle;
                        table.SetSize(rows, cols);
                        table.Position = new Point3d(0, 0, 0);
                        table.SetRowHeight(rowHeight);
                        for (int c = 0; c < cols; c++)
                            table.SetColumnWidth(c, colWidth);

                        // Set text height for all cells (smaller, but readable)
                        double textHeight = 100.0;
                        for (int r = 0; r < rows; r++)
                            for (int c = 0; c < cols; c++)
                                table.Cells[r, c].TextHeight = textHeight;

                        // Header row
                        table.Cells[0, 0].TextString = "ID";
                        table.Cells[0, 1].TextString = "Name";
                        table.Cells[0, 2].TextString = "Client";
                        table.Cells[0, 3].TextString = "Type";
                        table.Cells[0, 4].TextString = "Due Date";
                        table.Cells[0, 5].TextString = "Status";
                        table.Cells[0, 6].TextString = "Info";

                        // Data rows
                        for (int i = 0; i < inDesign.Count; i++)
                        {
                            var p = inDesign[i];
                            table.Cells[i + 1, 0].TextString = p["id"]?.ToString() ?? "";
                            table.Cells[i + 1, 1].TextString = p["name"]?.ToString() ?? "";
                            table.Cells[i + 1, 2].TextString = p["client"]?.ToString() ?? "";
                            table.Cells[i + 1, 3].TextString = p["type"]?.ToString() ?? "";
                            table.Cells[i + 1, 4].TextString = p["due_date"]?.ToString() ?? "";
                            table.Cells[i + 1, 5].TextString = p["status"]?.ToString() ?? "";
                            table.Cells[i + 1, 6].TextString = p["info"]?.ToString() ?? "";
                        }

                        btr.AppendEntity(table);
                        tr.AddNewlyCreatedDBObject(table, true);
                        tr.Commit();
                    }


                    ed.WriteMessage("\nTable drawn in ModelSpace.");
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\nError fetching project list: {ex.Message}");
            }
        }
    }
}