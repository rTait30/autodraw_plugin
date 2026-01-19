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

using DBPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

using Exception = System.Exception;

using Autodesk.AutoCAD.GraphicsInterface;

using GraphicsPolyline = Autodesk.AutoCAD.GraphicsInterface.Polyline;
using System.IO;

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
                DBPolyline fabric = new DBPolyline(4);
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
                    DBPolyline fabric = new DBPolyline(4);
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
                DBPolyline fabric = new DBPolyline(4);
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
            DBPolyline rect = new DBPolyline(4);
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
                DBPolyline pline = tr.GetObject(id, OpenMode.ForRead) as DBPolyline;
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
                    DBPolyline rect = new DBPolyline();
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

    public class ProjectDetails
    {
        public int id { get; set; }

        public Product product { get; set; }
        public ProjectGeneralInfo general { get; set; }

        public string info ()
        {

            string info = "Project id: " + id + "\n" + product.info() + "\n" + general.info();

            return info;
        }
    }

    public class Product

    {
        public int id { get; set; }
        public string name { get; set; }

        public string info()
        {
            return "Product ID: " + id + " (" + name + ")";
        }
    }

    public class ProjectGeneralInfo
    {
        public string client_id { get; set; }
        public string client_name { get; set; }

        public string name { get; set; }

        public string info()
        {
            return "Project name: " + name + " | Client: " + client_id + " (" + client_name + ")";
        }

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
        public static string BaseUrl { get; } = "http://127.0.0.1:5001/copelands/api";

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

        [CommandMethod("ADLOGIN")] // Renamed from SHADE_LOGIN
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

                    Database db = doc.Database;

                    // Lock the document before making changes
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                        MText mtext = new MText();
                        mtext.TextHeight = 1000;
                        mtext.Contents = $"Hello {data.username}, {data.role}";
                        mtext.Location = new Point3d(0, 2000, 0);

                        btr.AppendEntity(mtext);
                        tr.AddNewlyCreatedDBObject(mtext, true);

                        tr.Commit();
                    }
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


        [CommandMethod("ADSTATUS")]
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
        //private static readonly HttpClient client = new HttpClient();
    
        [CommandMethod("ADSTARTOLD")] // Renamed from SHADE_GET_GEO
        public async void ImportGeometryCmd()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            if (!PluginSession.IsLoggedIn)
            {
                ed.WriteMessage("\nPlease login first (ADLOGIN).");
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

        // 2. LOCAL STORAGE (Static so it persists between commands)
        public static int CurrentProjectId = 0;
        public static ProjectDetails CurrentProjectData = null;

        // ROOT OBJECT
        public class ProjectDetails
        {
            public object project_attributes { get; set; }
            public object product_attributes { get; set; }
            public AutoDrawConfig autodraw_config { get; set; }
            public AutoDrawMeta autodraw_meta { get; set; }
            public AutoDrawRecord autodraw_record { get; set; } // Keep as generic object for now
        }

        // META DATA
        public class AutoDrawMeta
        {
            public int current_step { get; set; }
            public int current_substep { get; set; }
            public bool is_complete { get; set; }
            public string last_updated { get; set; }
        }

        // CONFIG STRUCTURE
        public class AutoDrawConfig
        {
            public int stepCount { get; set; }
            public List<ConfigStep> steps { get; set; }
        }

        public class ConfigStep
        {
            public string key { get; set; }
            public string label { get; set; }
            // NEW: The rules for what to draw in this step
            public List<ShowRule> show { get; set; }
            public List<ConfigSubstep> substeps { get; set; }
        }

        public class ShowRule
        {
            public string query { get; set; } // e.g., "ad_layer"
            public string value { get; set; } // e.g., "AD_STRUCTURE"
        }

        public class ConfigSubstep
        {
            public string key { get; set; }
            public string label { get; set; }
            public string method { get; set; }
            public bool automated { get; set; }
        }

        // --- TOP LEVEL RECORD ---
        public class AutoDrawRecord
        {
            public string created_at { get; set; }

            // This list can now hold LineItems, CircleItems, etc. mixed together
            public List<GeometryItem> geometry { get; set; }
        }

        // This tells Newtonsoft: "Use this class to decide what object to create"
        public class GeometryItemConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return (objectType == typeof(GeometryItem));
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                // 1. Load the data into a generic JSON Object
                JObject jo = JObject.Load(reader);

                // 2. Read the "type" property
                string type = (string)jo["type"];
                GeometryItem item = null;

                // 3. Decide which class to instantiate
                switch (type)
                {
                    case "geo_line":
                        item = new LineItem();
                        break;
                    // Add cases here as you expand (e.g., "geo_circle")
                    // case "geo_circle": 
                    //    item = new CircleItem(); 
                    //    break;
                    default:
                        // Fallback if we don't recognize the type
                        item = new GeometryItem();
                        break;
                }

                // 4. Fill the empty object with the data
                serializer.Populate(jo.CreateReader(), item);
                return item;
            }

            public override bool CanWrite => false; // We only need this for reading
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotImplementedException();
        }

        // --- BASE CLASS (Abstract) ---
        // The [JsonConverter] tag connects the "Traffic Cop" we wrote above
        [JsonConverter(typeof(GeometryItemConverter))]
        public class GeometryItem
        {
            public string id { get; set; }
            public string key { get; set; }
            public string ad_layer { get; set; }
            public int product_index { get; set; }
            public List<string> tags { get; set; }
            public string type { get; set; } // "geo_line"
        }

        // --- TYPE 1: LINE ---
        public class LineItem : GeometryItem
        {
            // Strongly typed attributes specifically for Lines
            public LineAttributes attributes { get; set; }
        }

        public class LineAttributes
        {
            public double[] start { get; set; }
            public double[] end { get; set; }

            // Helper to get AutoCAD points directly
            public Point3d StartPoint => new Point3d(start[0], start[1], start.Length > 2 ? start[2] : 0);
            public Point3d EndPoint => new Point3d(end[0], end[1], end.Length > 2 ? end[2] : 0);
        }

        // Reusing your client instance
        private static readonly HttpClient client = new HttpClient();

        public class Commands
        {
            public static int CurrentProjectId = 0;
            public static ProjectDetails CurrentProjectData = null;
            private static readonly HttpClient client = new HttpClient();

            [CommandMethod("ADSTART")]
            public async void StartAutoDraw()
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                Editor ed = doc.Editor;
                Database db = doc.Database;

                if (!PluginSession.IsLoggedIn) { ed.WriteMessage("\nPlease login first (ADLOGIN)."); return; }

                try
                {
                    // A. GET ID
                    PromptStringOptions idOpts = new PromptStringOptions("\nEnter Project ID: ");
                    idOpts.AllowSpaces = false;
                    PromptResult idRes = ed.GetString(idOpts);
                    if (idRes.Status != PromptStatus.OK) return;
                    CurrentProjectId = int.Parse(idRes.StringResult);

                    // B. FETCH
                    string url = $"{PluginSession.BaseUrl}/automation/start/{CurrentProjectId}";
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PluginSession.AuthToken);
                    string json = await client.GetStringAsync(url);
                    CurrentProjectData = JsonConvert.DeserializeObject<ProjectDetails>(json);

                    // C. DRAW
                    using (DocumentLock docLock = doc.LockDocument())
                    {
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                            // 1. DRAW STATUS BOARD (Left Side)
                            // Moved to -15,000 so it doesn't touch the big boxes
                            DrawStatusBoard(tr, btr, CurrentProjectData.autodraw_config, CurrentProjectData.autodraw_meta, new Point3d(-15000, 0, 0));

                            // 2. DRAW WORKFLOW BOXES (Center, Downwards)
                            DrawWorkflowBoxes(tr, btr, CurrentProjectData.autodraw_config, CurrentProjectData.autodraw_meta, CurrentProjectData.autodraw_record);

                            // 3. DRAW DEBUG ROW (Top, Rightwards)
                            DrawDebugRow(tr, btr, CurrentProjectData);

                            tr.Commit();
                        }
                    }
                    ed.Regen();
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\nError: {ex.Message}");
                }
            }

            [CommandMethod("ADCONTINUE")]
            public async void ContinueAutoDraw()
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                Editor ed = doc.Editor;
                Database db = doc.Database;

                if (!PluginSession.IsLoggedIn)
                {
                    ed.WriteMessage("\nPlease login first (ADLOGIN).");
                    return;
                }

                if (CurrentProjectId == 0)
                {
                    ed.WriteMessage("\nNo project cached. Run ADSTART first.");
                    return;
                }

                try
                {
                    // Prepare POST request (empty body for now)
                    string url = $"{PluginSession.BaseUrl}/automation/continue/{CurrentProjectId}";
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", PluginSession.AuthToken);
                    var content = new StringContent("", Encoding.UTF8, "application/json");
                    HttpResponseMessage response = await client.PostAsync(url, content);

                    if (!response.IsSuccessStatusCode)
                    {
                        ed.WriteMessage($"\nRequest Failed: {response.StatusCode}");
                        return;
                    }

                    string json = await response.Content.ReadAsStringAsync();

                    // Parse the wrapper and extract "data"
                    var wrapper = JObject.Parse(json);
                    var dataToken = wrapper["data"];
                    if (dataToken == null)
                    {
                        ed.WriteMessage("\nAPI response missing 'data' property.");
                        return;
                    }

                    var updated = dataToken.ToObject<ProjectDetails>();
                    if (updated == null)
                    {
                        ed.WriteMessage("\nFailed to parse updated project data.");
                        return;
                    }

                    // Update cached data (except autodraw_config)
                    if (CurrentProjectData == null)
                        CurrentProjectData = updated;
                    else
                    {
                        CurrentProjectData.project_attributes = updated.project_attributes;
                        CurrentProjectData.product_attributes = updated.product_attributes;
                        CurrentProjectData.autodraw_meta = updated.autodraw_meta;
                        CurrentProjectData.autodraw_record = updated.autodraw_record;
                        // Do NOT update autodraw_config
                    }

                    // Redraw everything except autodraw_config (i.e., do not redraw status board)
                    using (DocumentLock docLock = doc.LockDocument())
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                        // Redraw workflow boxes and debug row only
                        DrawWorkflowBoxes(tr, btr, CurrentProjectData.autodraw_config, CurrentProjectData.autodraw_meta, CurrentProjectData.autodraw_record);
                        DrawDebugRow(tr, btr, CurrentProjectData);

                        tr.Commit();
                    }
                    ed.Regen();
                    ed.WriteMessage("\nADCONTINUE: Updated and redrew project data.");
                }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\nError: {ex.Message}");
                }
            }



            // --- 1. STATUS BOARD (Simple Text List) ---
            private void DrawStatusBoard(Transaction tr, BlockTableRecord btr, AutoDrawConfig config, AutoDrawMeta meta, Point3d startPt)
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
                        subText.ColorIndex = isCurrentSub ? 1 : 7;
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

            // --- 2. WORKFLOW BOXES (30k x 30k) ---
            private void DrawWorkflowBoxes(Transaction tr, BlockTableRecord btr, AutoDrawConfig config, AutoDrawMeta meta, AutoDrawRecord record)
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

                    // 1. Draw 30k Box
                    DBPolyline box = new DBPolyline();
                    box.AddVertexAt(0, new Point2d(0, currentY), 0, 0, 0);
                    box.AddVertexAt(1, new Point2d(boxSize, currentY), 0, 0, 0);
                    box.AddVertexAt(2, new Point2d(boxSize, currentY - boxSize), 0, 0, 0);
                    box.AddVertexAt(3, new Point2d(0, currentY - boxSize), 0, 0, 0);
                    box.Closed = true;
                    box.ColorIndex = colorIndex;
                    if (isActiveStep) box.ConstantWidth = 250; // Thicker border for active

                    btr.AppendEntity(box);
                    tr.AddNewlyCreatedDBObject(box, true);

                    // 2. Draw Title inside box
                    MText title = new MText();
                    title.Contents = $"{{\\H500;Step {i}: {step.label}}}";
                    title.Location = new Point3d(500, currentY - 500, 0);
                    title.TextHeight = 500;
                    title.ColorIndex = isActiveStep ? 1 : 7;
                    btr.AppendEntity(title);
                    tr.AddNewlyCreatedDBObject(title, true);

                    // Define the Top-Left of this step's area
                    


                    if (record != null && record.geometry != null)
                    {
                        DrawStepGeometry(tr, btr, step, record.geometry, stepOrigin);
                    }

                    currentY -= (boxSize + gap);
                }
            }

            public static class AutoDrawVisualizer
            {
                // CONSTANTS
                private const string UI_LAYER = "AD_UI_OVERLAY";
                private const double TEXT_PADDING = 200.0;
                private const double BOX_PADDING = 400.0;

                /// <summary>
                /// Clears all existing debug UI elements and ensures the layer exists.
                /// Call this at the start of any draw command.
                /// </summary>
                public static void ClearAndPrepareLayer(Transaction tr, Database db)
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                    // 1. Create/Check Layer
                    if (!lt.Has(UI_LAYER))
                    {
                        lt.UpgradeOpen();
                        using (LayerTableRecord ltr = new LayerTableRecord())
                        {
                            ltr.Name = UI_LAYER;
                            ltr.IsPlottable = false; // Never print debug info
                            lt.Add(ltr);
                            tr.AddNewlyCreatedDBObject(ltr, true);
                        }
                    }

                    // 2. Delete existing entities on this layer
                    // (Uses a fast TypedValue filter)
                    var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.LayerName, UI_LAYER) });
                    var result = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument.Editor.SelectAll(filter);

                    if (result.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                    {
                        foreach (ObjectId id in result.Value.GetObjectIds())
                        {
                            Entity ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                            ent.Erase();
                        }
                    }
                }

                /// <summary>
                /// Draws the horizontal row of raw JSON data starting at x: -100,000
                /// </summary>
                public static void DrawDebugRow(Transaction tr, BlockTableRecord btr, ProjectDetails data)
                {
                    double currentX = -100000;
                    double yPos = 0;
                    double gap = 2000; // Gap between boxes

                    // Serialize
                    string pAttr = JsonConvert.SerializeObject(data.project_attributes, Formatting.Indented);
                    string prodAttr = JsonConvert.SerializeObject(data.product_attributes, Formatting.Indented);
                    string config = JsonConvert.SerializeObject(data.autodraw_config, Formatting.Indented);
                    string meta = JsonConvert.SerializeObject(data.autodraw_meta, Formatting.Indented);
                    string record = JsonConvert.SerializeObject(data.autodraw_record, Formatting.Indented);

                    // Draw sequence
                    currentX += DrawDebugBox(tr, btr, "PROJECT ATTRIBUTES", pAttr, new Point3d(currentX, yPos, 0), 1) + gap;
                    currentX += DrawDebugBox(tr, btr, "PRODUCT ATTRIBUTES", prodAttr, new Point3d(currentX, yPos, 0), 2) + gap;
                    currentX += DrawDebugBox(tr, btr, "AUTODRAW CONFIG", config, new Point3d(currentX, yPos, 0), 3) + gap;
                    currentX += DrawDebugBox(tr, btr, "AUTODRAW META", meta, new Point3d(currentX, yPos, 0), 4) + gap;
                    currentX += DrawDebugBox(tr, btr, "AUTODRAW RECORD", record, new Point3d(currentX, yPos, 0), 6) + gap;
                }

                /// <summary>
                /// Internal Helper to draw the box and text
                /// </summary>
                private static double DrawDebugBox(Transaction tr, BlockTableRecord btr, string title, string content, Point3d position, int colorIndex)
                {
                    // 1. Create Text
                    MText mtext = new MText();
                    mtext.Contents = $"{{\\H400;\\C7;{title}}}\n\\P{content}";
                    mtext.Location = new Point3d(position.X + TEXT_PADDING, position.Y - TEXT_PADDING, 0);
                    mtext.TextHeight = 200.0;
                    mtext.Width = 0.0; // No wrap
                    mtext.Layer = UI_LAYER; // Important: Assign to UI Layer

                    btr.AppendEntity(mtext);
                    tr.AddNewlyCreatedDBObject(mtext, true);

                    // 2. Calculate Box Size
                    Extents3d ext = mtext.GeometricExtents;
                    double w = ext.MaxPoint.X - ext.MinPoint.X + (TEXT_PADDING * 2);
                    double h = ext.MaxPoint.Y - ext.MinPoint.Y + (TEXT_PADDING * 2);

                    // 3. Create Rectangle (Polyline)
                    DPolyline box = new Polyline();
                    box.AddVertexAt(0, new Point2d(position.X, position.Y), 0, 0, 0);
                    box.AddVertexAt(1, new Point2d(position.X + w, position.Y), 0, 0, 0);
                    box.AddVertexAt(2, new Point2d(position.X + w, position.Y - h), 0, 0, 0);
                    box.AddVertexAt(3, new Point2d(position.X, position.Y - h), 0, 0, 0);
                    box.Closed = true;
                    box.ColorIndex = colorIndex;
                    box.Layer = UI_LAYER; // Important: Assign to UI Layer

                    btr.AppendEntity(box);
                    tr.AddNewlyCreatedDBObject(box, true);

                    return w;
                }
            }
        }

        /// <summary>
        /// Draws the geometry for a specific step based on its "show" rules.
        /// </summary>
        /// <param name="tr">Active Transaction</param>
        /// <param name="btr">ModelSpace Record</param>
        /// <param name="step">The config for the current step</param>
        /// <param name="allGeometry">The full list of geometry from the record</param>
        /// <param name="offset">The Top-Left corner of the Step Box (e.g., 0, -35000)</param>
        public static void DrawStepGeometry(Transaction tr, BlockTableRecord btr, ConfigStep step, List<GeometryItem> allGeometry, Point3d offset)
        {
            // --- LOCAL OFFSETS ---
            double debugOffsetX = 15000;   // Debug info X offset from box origin
            double debugOffsetY = -2000;   // Debug info Y offset from box origin
            double drawOffsetX = 2000;     // Geometry X offset from box origin
            double drawOffsetY = -2000;    // Geometry Y offset from box origin

            // --- DEBUG SETUP: Start writing text at the top-left of the box, with local offset ---
            double debugY = offset.Y + debugOffsetY;
            double debugX = offset.X + debugOffsetX;

            // --- Draw crosses to indicate the true start positions (before offsets) ---
            void DrawCross(Point3d center, double size, int color)
            {
                double half = size / 2;
                Line l1 = new Line(
                    new Point3d(offset.X + center.X - half, offset.Y + center.Y, 0),
                    new Point3d(offset.X + center.X + half, offset.Y + center.Y, 0));
                Line l2 = new Line(
                    new Point3d(offset.X + center.X, offset.Y + center.Y - half, 0),
                    new Point3d(offset.X + center.X, offset.Y + center.Y + half, 0));
                l1.ColorIndex = color;
                l2.ColorIndex = color;
                btr.AppendEntity(l1); tr.AddNewlyCreatedDBObject(l1, true);
                btr.AppendEntity(l2); tr.AddNewlyCreatedDBObject(l2, true);
            }

            // Draw cross at the original geometry start (offset)
            DrawCross(new Point3d (drawOffsetX, drawOffsetY, 0), 1200, 2); // Green cross for geometry start

            // Draw cross at the original debug info start
            DrawCross(new Point3d(debugOffsetX, debugOffsetY, 0), 1200, 1); // Red cross for debug info start

            // Local Helper to draw debug text quickly
            void LogToModel(string msg, int color)
            {
                MText txt = new MText();
                txt.Contents = msg;
                txt.Location = new Point3d(debugX, debugY, 0);
                txt.TextHeight = 200; // Readable size
                txt.ColorIndex = color; // 1=Red, 3=Green, 7=White
                btr.AppendEntity(txt);
                tr.AddNewlyCreatedDBObject(txt, true);
                debugY -= 300; // Move down for next line
            }

            try
            {
                // 1. Log Entry
                LogToModel($"--- DEBUG: {step.label} ---", 7);

                // 2. Check Rules
                if (step.show == null || step.show.Count == 0)
                {
                    LogToModel("No 'show' rules defined. Exiting.", 1);
                    return;
                }
                LogToModel($"Rules Found: {step.show.Count}", 7);

                // 3. Check Geometry Count
                if (allGeometry == null || allGeometry.Count == 0)
                {
                    LogToModel("No geometry in record. Exiting.", 1);
                    return;
                }
                LogToModel($"Total Geometry Items: {allGeometry.Count}", 7);

                // 4. Iterate
                int drawCount = 0;
                foreach (var item in allGeometry)
                {
                    bool shouldDraw = false;
                    string matchedRule = "";

                    foreach (var rule in step.show)
                    {
                        if (rule.query == "ad_layer" && item.ad_layer == rule.value)
                        {
                            shouldDraw = true;
                            matchedRule = rule.value;
                            break;
                        }
                    }

                    if (shouldDraw)
                    {
                        Entity ent = null;

                        if (item is LineItem lineItem)
                        {
                            if (lineItem.attributes == null)
                            {
                                LogToModel($"ERROR: Item {item.id} has no attributes!", 1);
                                continue;
                            }

                            // Apply local geometry offset
                            Point3d localStart = lineItem.attributes.StartPoint.Add(new Vector3d(drawOffsetX, drawOffsetY, 0));
                            Point3d localEnd = lineItem.attributes.EndPoint.Add(new Vector3d(drawOffsetX, drawOffsetY, 0));

                            ent = new Line(localStart, localEnd);
                        }
                        else
                        {
                            LogToModel($"SKIP: Item is {item.GetType().Name}, not LineItem", 1);
                        }

                        if (ent != null)
                        {
                            LayerTable lt = (LayerTable)tr.GetObject(btr.Database.LayerTableId, OpenMode.ForRead);
                            if (!lt.Has(item.ad_layer))
                            {
                                LogToModel($"ERROR: Layer '{item.ad_layer}' missing in DWG!", 1);
                            }
                            else
                            {
                                ent.Layer = item.ad_layer;
                            }

                            // Apply the main offset as before
                            ent.TransformBy(Matrix3d.Displacement(offset.GetAsVector()));

                            btr.AppendEntity(ent);
                            tr.AddNewlyCreatedDBObject(ent, true);
                            drawCount++;
                        }
                    }
                }

                LogToModel($"Successfully Drawn: {drawCount} items", 3);
            }
            catch (System.Exception ex)
            {
                LogToModel($"CRASH: {ex.Message}", 1);
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

                // --- 2. Draw all lines on ADWORK_LINE, just below, and a line between the 2 highest workpoints ---
                if (minY.HasValue && maxY.HasValue)
                {
                    double padding = 200.0; // You can adjust this value
                    double yOffset = minY.Value - (maxY.Value - minY.Value) - padding;

                    EnsureLayerExists(tr, db, "AD_WORK_LINE");

                    // 1. Draw all lines on ADWORK_LINE
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
                ed.WriteMessage($"\n--- Also drew lines on ADWORK_LINE below original drawing. ---");
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
        [CommandMethod("ADLIST")]
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

    public class DynamicLineDemo
    {
        [CommandMethod("MoveLineRandomGrid")]
        public void MoveLineRandomGrid()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            // Grid settings
            int gridSize = 200;
            int cellSize = 100;
            int steps = 1000;

            // Draw the bounding box once
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                DBPolyline box = new DBPolyline(4);
                box.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                box.AddVertexAt(1, new Point2d(gridSize * cellSize, 0), 0, 0, 0);
                box.AddVertexAt(2, new Point2d(gridSize * cellSize, gridSize * cellSize), 0, 0, 0);
                box.AddVertexAt(3, new Point2d(0, gridSize * cellSize), 0, 0, 0);
                box.Closed = true;
                box.ColorIndex = 1; // Red
                btr.AppendEntity(box);
                tr.AddNewlyCreatedDBObject(box, true);

                tr.Commit();
            }

            // Start at the center of the grid
            int x = gridSize / 2;
            int y = gridSize / 2;
            ObjectId lineId = ObjectId.Null;
            Random rand = new Random();

            for (int i = 0; i < steps; i++)
            {
                // Choose a random direction: 0=up, 1=down, 2=left, 3=right
                int dir = rand.Next(4);
                switch (dir)
                {
                    case 0: if (y < gridSize - 1) y++; break; // up
                    case 1: if (y > 0) y--; break;            // down
                    case 2: if (x > 0) x--; break;            // left
                    case 3: if (x < gridSize - 1) x++; break; // right
                }

                Point3d start = new Point3d(x * cellSize, y * cellSize, 0);
                Point3d end = new Point3d(start.X + cellSize, start.Y, 0); // Horizontal segment

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Line line = new Line(start, end);
                    line.ColorIndex = 2; // Yellow
                    line.LineWeight = LineWeight.LineWeight200;
                    lineId = btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    tr.Commit();
                }

                Application.UpdateScreen();
                ed.Regen();
                ed.WriteMessage($"\nStep {i + 1}: Line at grid ({x},{y})");

                System.Threading.Thread.Sleep(33);

                // Erase previous line
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (!lineId.IsNull)
                    {
                        Entity ent = tr.GetObject(lineId, OpenMode.ForWrite) as Entity;
                        if (ent != null)
                            ent.Erase();
                    }
                    tr.Commit();
                }
            }
        }

        [CommandMethod("MoveLineMouseGrid")]
        public void MoveLineMouseGrid()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            int gridSize = 20;
            int cellSize = 100;
            int steps = 10;

            // Draw the bounding box once
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                DBPolyline box = new DBPolyline(4);
                box.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
                box.AddVertexAt(1, new Point2d(gridSize * cellSize, 0), 0, 0, 0);
                box.AddVertexAt(2, new Point2d(gridSize * cellSize, gridSize * cellSize), 0, 0, 0);
                box.AddVertexAt(3, new Point2d(0, gridSize * cellSize), 0, 0, 0);
                box.Closed = true;
                box.ColorIndex = 1; // Red
                btr.AppendEntity(box);
                tr.AddNewlyCreatedDBObject(box, true);

                tr.Commit();
            }

            int x = gridSize / 2;
            int y = gridSize / 2;
            ObjectId lineId = ObjectId.Null;

            // Gamepad layout (centered below grid)
            int padBoxSize = cellSize;
            int padSpacing = cellSize / 4;
            int padCenterX = gridSize * cellSize / 2;
            int padCenterY = gridSize * cellSize + padBoxSize * 2 + padSpacing;

            // Box centers: Up, Down, Left, Right (relative to pad center)
            Point3d[] padCenters = new[]
            {
        new Point3d(padCenterX, padCenterY + padBoxSize + padSpacing, 0), // Up
        new Point3d(padCenterX, padCenterY - padBoxSize - padSpacing, 0), // Down
        new Point3d(padCenterX - padBoxSize - padSpacing, padCenterY, 0), // Left
        new Point3d(padCenterX + padBoxSize + padSpacing, padCenterY, 0), // Right
    };

            // Arrow directions for each box
            Vector3d[] arrowDirs = new[]
            {
        new Vector3d(0, 1, 0),   // Up
        new Vector3d(0, -1, 0),  // Down
        new Vector3d(-1, 0, 0),  // Left
        new Vector3d(1, 0, 0),   // Right
    };

            for (int i = 0; i < steps; i++)
            {
                // Draw transient selection boxes and arrows
                List<TransientDrawable> transients = new List<TransientDrawable>();
                for (int d = 0; d < 4; d++)
                {
                    // Draw box
                    Point3d c = padCenters[d];
                    Point3d[] corners = new[]
                    {
                new Point3d(c.X - padBoxSize / 2, c.Y - padBoxSize / 2, 0),
                new Point3d(c.X + padBoxSize / 2, c.Y - padBoxSize / 2, 0),
                new Point3d(c.X + padBoxSize / 2, c.Y + padBoxSize / 2, 0),
                new Point3d(c.X - padBoxSize / 2, c.Y + padBoxSize / 2, 0),
                new Point3d(c.X - padBoxSize / 2, c.Y - padBoxSize / 2, 0)
            };
                    for (int j = 0; j < 4; j++)
                        transients.Add(new TransientDrawable(new Line(corners[j], corners[j + 1])));

                    // Draw arrow
                    Point3d arrowStart = c;
                    Point3d arrowEnd = c + arrowDirs[d] * (padBoxSize / 2 - 15);
                    transients.Add(new TransientDrawable(new Line(arrowStart, arrowEnd)));
                    // Arrow head
                    Vector3d headDir = arrowDirs[d];
                    Vector3d left = headDir.RotateBy(Math.PI * 3 / 4, Vector3d.ZAxis).GetNormal() * (padBoxSize / 6);
                    Vector3d right = headDir.RotateBy(-Math.PI * 3 / 4, Vector3d.ZAxis).GetNormal() * (padBoxSize / 6);
                    transients.Add(new TransientDrawable(new Line(arrowEnd, arrowEnd + left)));
                    transients.Add(new TransientDrawable(new Line(arrowEnd, arrowEnd + right)));
                }

                // Prompt user to click a box
                PromptPointOptions ppo = new PromptPointOptions($"\nStep {i + 1}: Click a direction box (ESC to quit):");
                PromptPointResult ppr = ed.GetPoint(ppo);

                // Remove transients
                foreach (var t in transients) t.Dispose();

                if (ppr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\nCancelled by user.");
                    break;
                }

                // Find which box the click is inside
                int chosen = -1;
                for (int d = 0; d < 4; d++)
                {
                    Point3d c = padCenters[d];
                    if (Math.Abs(ppr.Value.X - c.X) <= padBoxSize / 2 && Math.Abs(ppr.Value.Y - c.Y) <= padBoxSize / 2)
                    {
                        chosen = d;
                        break;
                    }
                }
                if (chosen == -1)
                {
                    ed.WriteMessage("\nClick was not inside any direction box. Try again.");
                    i--;
                    continue;
                }

                // Move according to chosen direction
                switch (chosen)
                {
                    case 0: if (y < gridSize - 1) y++; break; // Up
                    case 1: if (y > 0) y--; break;            // Down
                    case 2: if (x > 0) x--; break;            // Left
                    case 3: if (x < gridSize - 1) x++; break; // Right
                }

                Point3d start = new Point3d(x * cellSize, y * cellSize, 0);
                Point3d end = new Point3d(start.X + cellSize, start.Y, 0); // Horizontal segment

                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Line line = new Line(start, end);
                    line.ColorIndex = 2; // Yellow
                    line.LineWeight = LineWeight.LineWeight200;
                    lineId = btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);

                    tr.Commit();
                }

                Application.UpdateScreen();
                ed.Regen();
                ed.WriteMessage($"\nStep {i + 1}: Line at grid ({x},{y})");

                System.Threading.Thread.Sleep(300);

                // Erase previous line
                using (DocumentLock docLock = doc.LockDocument())
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    if (!lineId.IsNull)
                    {
                        Entity ent = tr.GetObject(lineId, OpenMode.ForWrite) as Entity;
                        if (ent != null)
                            ent.Erase();
                    }
                    tr.Commit();
                }
            }
        }



    }
}

// Helper for transient arrow drawing
class TransientDrawable : IDisposable
{
    private readonly Line _line;
    public TransientDrawable(Line line)
    {
        _line = line;
        Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.AddTransient(
            _line, TransientDrawingMode.DirectShortTerm, 128, new IntegerCollection());
    }
    public void Dispose()
    {
        Autodesk.AutoCAD.GraphicsInterface.TransientManager.CurrentTransientManager.EraseTransient(_line, new IntegerCollection());
        _line.Dispose();
    }
}

