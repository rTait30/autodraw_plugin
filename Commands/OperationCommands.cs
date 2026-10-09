using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Operations;
using autodraw_plugin.Services;

using Exception = System.Exception;

namespace autodraw_plugin.Commands;

/// <summary>
/// ADDO: run one of the server's operations, a recipe of them, or a list typed
/// in - several names separated by commas, run in turn once without being saved.
///
/// With a project chosen (ADDOPROJECT) it runs on the project: the job answers
/// what it can, the drawing goes back with the run where it was laid in here,
/// and the project's new drawing is laid in whole - its earlier states beside
/// it as pictures when stacking. ADDOBACK and ADDOFORWARD move along its
/// history.
///
/// With none it is a free run on what is in the drawing: nothing is loaded or
/// recorded, the selection goes up, the operation's result comes back, and
/// with Layout Replace it takes the place of exactly what was sent. With Layout
/// Stack what was sent stays, and the result is laid beside it.
///
/// ADDODATA names a project a free run takes its answers from, storing nothing
/// on it. ADDOSAVE saves the last runs on the chosen project as a recipe, and
/// ADRECIPE makes or changes one from a line of steps typed in.
///
/// What a value means is the operation's business. It says what it needs and
/// of what type, and this only picks the prompt to ask it with.
/// </summary>
public class OperationCommands
{
    [CommandMethod("ADDO")]
    public async void RunOperation()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return;
        }
        if (OperationService.ProjectId.HasValue)
        {
            await RunOnProject(doc, OperationService.ProjectId.Value);
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"autodraw_op_{Guid.NewGuid():N}.dxf");
        try
        {
            OperationCatalogueDTO catalogue = await OperationService.Catalogue();
            string? chosen = ChooseOperation(ed, catalogue);
            if (chosen == null) { ed.WriteMessage("\nCancelled."); return; }

            // One that needs nothing drawn - it draws a job from its data
            // (ADDODATA) - is sent an empty drawing without asking.
            OperationDTO? picked = catalogue.Operations.Find(o => o.Name == chosen || o.Id == chosen);
            ObjectId[]? sent = picked != null && picked.NeedsNothingDrawn
                ? Array.Empty<ObjectId>()
                : SelectToSend(ed, db);
            if (sent == null) { ed.WriteMessage("\nCancelled."); return; }

            using (doc.LockDocument())
            {
                // An ObjectIdCollection made from an empty array throws, so
                // nothing is sent as an empty collection.
                OperationService.Export(db, sent.Length > 0 ? new ObjectIdCollection(sent)
                                                            : new ObjectIdCollection(), path);
            }
            ed.WriteMessage($"\nSending {sent.Length} entities to {chosen}...");

            // The server keeps nothing between calls, so each answer goes up
            // with the drawing again until it has all it asked for.
            var inputs = new JObject();
            int floor = PieceGroupService.HighestObject(db);
            OperationResultDTO result = await OperationService.Run(chosen, path, inputs, floor);
            while (result.Error == "needs_input" && result.Inputs != null)
            {
                SayStep(ed, result);
                if (!Ask(ed, result.Inputs, inputs)) { ed.WriteMessage("\nCancelled."); return; }
                result = await OperationService.Run(chosen, path, inputs, floor);
            }

            foreach (string line in result.Interpreted ?? new List<string>())
                ed.WriteMessage("\n  " + line);

            if (!string.IsNullOrEmpty(result.Error))
            {
                SayStep(ed, result);
                ed.WriteMessage($"\nStopped: {result.Message ?? result.Error}");
                return;
            }
            SayRan(ed, result);

            bool replacing = !string.Equals(PluginSettings.Current.Layout, "Stack",
                                            StringComparison.OrdinalIgnoreCase);
            int erased = 0, imported;
            using (doc.LockDocument())
            {
                if (replacing)
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        erased = OperationService.Erase(tr, sent);
                        tr.Commit();
                    }
                }

                imported = DxfTransferService.ImportBase64(db, result.Drawing ?? "");

                // Cloned in with new handles, so the pieces need their groups
                // made again - see PieceGroupService.
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    PieceGroupService.Rebuild(tr, db);
                    tr.Commit();
                }
            }
            ed.Regen();

            if (!string.IsNullOrWhiteSpace(result.Notes)) ed.WriteMessage("\n" + result.Notes);
            foreach (string warning in result.Warnings ?? new List<string>())
                ed.WriteMessage("\nWarning: " + warning);
            ed.WriteMessage(replacing
                ? $"\nReplaced {erased} with {imported}."
                : $"\nDrew {imported} beside what was sent.");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>Choose the project ADDO runs on, and lay its drawing in. Enter for none.</summary>
    [CommandMethod("ADDOPROJECT")]
    public async void ChooseProject()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        PromptStringOptions options = new PromptStringOptions(
            "\nProject to run operations on, or Enter for free runs on the drawing: ")
        { AllowSpaces = false };
        PromptResult said = ed.GetString(options);
        if (said.Status != PromptStatus.OK) return;

        if (string.IsNullOrWhiteSpace(said.StringResult))
        {
            OperationService.ProjectId = null;
            OperationService.Loaded = false;
            ed.WriteMessage("\nADDO now runs on the drawing as it is.");
            return;
        }
        if (!int.TryParse(said.StringResult, out int projectId))
        {
            ed.WriteMessage("\nNot a project number.");
            return;
        }
        try
        {
            OperationResultDTO drawn = await OperationService.ProjectDrawing(projectId);
            if (!string.IsNullOrEmpty(drawn.Error)) { ed.WriteMessage($"\n{drawn.Message ?? drawn.Error}"); return; }
            OperationService.ProjectId = projectId;
            LayIn(doc, drawn);
            ed.WriteMessage($"\nADDO now runs on project {projectId}.");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// Choose a project free runs take their answers from - its fabric, its
    /// quantities - without running on it or storing anything there. Enter for none.
    /// </summary>
    [CommandMethod("ADDODATA")]
    public void ChooseDataProject()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        PromptStringOptions options = new PromptStringOptions(
            "\nProject free runs take their answers from, or Enter for none: ")
        { AllowSpaces = false };
        PromptResult said = ed.GetString(options);
        if (said.Status != PromptStatus.OK) return;
        if (string.IsNullOrWhiteSpace(said.StringResult))
        {
            OperationService.DataProjectId = null;
            ed.WriteMessage("\nFree runs now ask for everything not in the drawing.");
            return;
        }
        if (!int.TryParse(said.StringResult, out int projectId))
        {
            ed.WriteMessage("\nNot a project number.");
            return;
        }
        OperationService.DataProjectId = projectId;
        ed.WriteMessage($"\nFree runs now take their answers from project {projectId}.");
    }

    /// <summary>
    /// Save the last operations run on the chosen project as a recipe: they
    /// become its steps, in order, and what was typed for them its defaults.
    /// </summary>
    [CommandMethod("ADDOSAVE")]
    public async void SaveRecipe()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        if (!OperationService.ProjectId.HasValue)
        {
            ed.WriteMessage("\nNo project chosen (ADDOPROJECT): a recipe is saved from a project's runs.");
            return;
        }
        PromptResult named = ed.GetString(new PromptStringOptions("\nRecipe name: ") { AllowSpaces = false });
        if (named.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(named.StringResult)) return;
        int? count = PickNumber(ed, "\nHow many of the last runs", 100, 3);
        if (count == null) return;
        try
        {
            SaySaved(ed, await OperationService.SaveRecipe(
                OperationService.ProjectId.Value, named.StringResult, count.Value));
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// Make a recipe, or change one, by typing its steps: names separated by
    /// commas as ADDO takes them, each with its answers in brackets where it
    /// has any - `seam_marks, label_panels(height=150)`. An existing recipe is
    /// shown that way first, so editing it is typing the line again; saving
    /// adds its next version and every earlier one stays as it was.
    /// </summary>
    [CommandMethod("ADRECIPE")]
    public async void EditRecipe()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return;
        }
        try
        {
            List<OperationDTO> saved = (await OperationService.Catalogue()).Operations
                .Where(listed => listed.Kind == "recipe").ToList();
            foreach (OperationDTO recipe in saved)
                ed.WriteMessage($"\n  {recipe.Id}: {recipe.Written}"
                                + (recipe.Broken != null ? $"  (cannot run: {recipe.Broken})" : ""));

            PromptResult named = ed.GetString(new PromptStringOptions(
                "\nRecipe name, new or from the list: ") { AllowSpaces = false });
            if (named.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(named.StringResult)) return;
            string name = named.StringResult.Trim();
            OperationDTO? held = saved.FirstOrDefault(recipe => recipe.Name == name);
            if (held != null)
                ed.WriteMessage($"\n{held.Id} runs: {held.Written}");

            PromptResult typed = ed.GetString(new PromptStringOptions(held != null
                ? "\nSteps, separated by commas (Enter to keep): "
                : "\nSteps, separated by commas: ") { AllowSpaces = true });
            if (typed.Status != PromptStatus.OK) return;
            string steps = typed.StringResult.Trim();
            if (steps.Length == 0)
            {
                if (held?.Written == null) return;
                steps = held.Written;
            }

            string? label = AskText(ed, "Label", held?.Label);
            if (label == null) return;
            string? description = AskText(ed, "Description", held?.Description);
            if (description == null) return;

            SaySaved(ed, await OperationService.SaveRecipe(name, steps, label, description));
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>A line of text, Enter to keep what is there; null when cancelled.</summary>
    private static string? AskText(Editor ed, string what, string? now)
    {
        PromptResult said = ed.GetString(new PromptStringOptions(string.IsNullOrEmpty(now)
            ? $"\n{what} (Enter for none): "
            : $"\n{what} <{now}> (Enter to keep): ") { AllowSpaces = true });
        return said.Status == PromptStatus.OK ? said.StringResult.Trim() : null;
    }

    /// <summary>A saved recipe as the server worked it out: what it runs, asks, needs and warns of.</summary>
    private static void SaySaved(Editor ed, RecipeSavedDTO saved)
    {
        if (!string.IsNullOrEmpty(saved.Error) || saved.Recipe == null)
        {
            ed.WriteMessage($"\nNot saved: {saved.Message ?? saved.Error}");
            return;
        }
        RecipeDTO recipe = saved.Recipe;
        ed.WriteMessage(saved.Created
            ? $"\nSaved {recipe.Id}: {recipe.Written ?? string.Join(", ", recipe.Operations)}"
            : $"\n{recipe.Id} already runs these steps; nothing new saved.");
        var roles = recipe.Needs?["roles"]?.ToObject<List<string>>() ?? new();
        var facts = recipe.Needs?["facts"]?.ToObject<List<string>>() ?? new();
        if (roles.Count + facts.Count > 0)
            ed.WriteMessage($"\n  Needs to start: {string.Join(", ", roles.Concat(facts))}");
        foreach (OperationInputDTO field in recipe.Inputs)
        {
            bool hasDefault = field.Default != null && field.Default.Type != JTokenType.Null;
            ed.WriteMessage($"\n  Asks: {field.Label ?? field.Key}" + (hasDefault ? $" (default {field.Default})" : ""));
        }
        foreach (string warning in recipe.Warnings) ed.WriteMessage("\n  Warning: " + warning);
    }

    [CommandMethod("ADDOBACK")]
    public async void StepBack() => await StepAlong(false);

    [CommandMethod("ADDOFORWARD")]
    public async void StepForward() => await StepAlong(true);

    private static async Task StepAlong(bool forward)
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        if (!OperationService.ProjectId.HasValue)
        {
            ed.WriteMessage("\nNo project chosen (ADDOPROJECT).");
            return;
        }
        try
        {
            OperationResultDTO moved = await OperationService.Step(OperationService.ProjectId.Value, forward);
            if (!string.IsNullOrEmpty(moved.Error)) ed.WriteMessage($"\n{moved.Message ?? moved.Error}");
            if (moved.Drawing != null) LayIn(doc, moved);
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// Run on the project: the drawing goes back with it if it was laid in by
    /// ADDO, the job answers what it can and anything else is asked, and the
    /// project's new drawing replaces this one whole.
    /// </summary>
    private static async Task RunOnProject(Document doc, int projectId)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;
        string path = Path.Combine(Path.GetTempPath(), $"autodraw_op_{Guid.NewGuid():N}.dxf");
        try
        {
            string? chosen = ChooseOperation(ed, await OperationService.Catalogue());
            if (chosen == null) { ed.WriteMessage("\nCancelled."); return; }

            string? sending = null;
            if (OperationService.Loaded)
            {
                ObjectId[] everything = AllOfModelspace(db)
                    .Where(id => !IsDecoration(db, id)).ToArray();
                using (doc.LockDocument())
                {
                    OperationService.Export(db, new ObjectIdCollection(everything), path);
                }
                sending = path;
            }
            ed.WriteMessage($"\nRunning {chosen} on project {projectId}...");

            var inputs = new JObject();
            OperationResultDTO result = await OperationService.RunOnProject(
                projectId, chosen, sending, inputs, OperationService.CurrentArtifact);
            while (result.Error == "needs_input" && result.Inputs != null)
            {
                // The drawing is kept with the first call; answers go without it.
                OperationService.CurrentArtifact = result.ArtifactId;
                SayStep(ed, result);
                if (!Ask(ed, result.Inputs, inputs)) { ed.WriteMessage("\nCancelled."); return; }
                result = await OperationService.RunOnProject(
                    projectId, chosen, null, inputs, OperationService.CurrentArtifact);
            }

            foreach (string line in result.Interpreted ?? new List<string>())
                ed.WriteMessage("\n  " + line);
            if (!string.IsNullOrEmpty(result.Error))
            {
                SayStep(ed, result);
                ed.WriteMessage($"\nStopped: {result.Message ?? result.Error}");
                if (result.Error == "stale") ed.WriteMessage("\nRun ADDOPROJECT to lay the project in again.");
                return;
            }

            LayIn(doc, result);
            SayRan(ed, result);
            if (!string.IsNullOrWhiteSpace(result.Notes)) ed.WriteMessage("\n" + result.Notes);
            foreach (string warning in result.Warnings ?? new List<string>())
                ed.WriteMessage("\nWarning: " + warning);
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Replace the drawing with the project's as just sent: everything but the
    /// decoration goes, the drawing comes in, and when stacking the pictures of
    /// its earlier states go on the history layer beside it.
    /// </summary>
    private static void LayIn(Document doc, OperationResultDTO drawn)
    {
        Database db = doc.Database;
        using (doc.LockDocument())
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DxfTransferService.EraseAllExcept(tr, db, AutoDrawVisualizer.DecorationLayers);
                AutoDrawVisualizer.EnsureLayer(tr, db, GridService.HistoryLayer);
                AutoDrawVisualizer.ClearLayer(tr, db, GridService.HistoryLayer);
                AutoDrawVisualizer.ClearLayer(tr, db, AutoDrawVisualizer.CaptionLayer);
                tr.Commit();
            }
            DxfTransferService.ImportBase64(db, drawn.Drawing ?? "");
            if (!string.IsNullOrEmpty(drawn.History)) DxfTransferService.ImportBase64(db, drawn.History);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                PieceGroupService.Rebuild(tr, db);
                tr.Commit();
            }
        }
        doc.Editor.Regen();
        OperationService.CurrentArtifact = drawn.ArtifactId;
        OperationService.Loaded = true;
    }

    private static bool IsDecoration(Database db, ObjectId id)
    {
        var decoration = new HashSet<string>(AutoDrawVisualizer.DecorationLayers,
                                             StringComparer.OrdinalIgnoreCase);
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            bool found = tr.GetObject(id, OpenMode.ForRead) is Entity ent && decoration.Contains(ent.Layer);
            tr.Commit();
            return found;
        }
    }

    /// <summary>
    /// An operation or recipe by its number in the list, or names typed in -
    /// several separated by commas run in turn, as a recipe saved nowhere,
    /// each with answers in brackets where it is given any.
    /// What is picked runs its newest version. Null when cancelled.
    /// </summary>
    private static string? ChooseOperation(Editor ed, OperationCatalogueDTO catalogue)
    {
        var listed = catalogue.Operations;
        if (listed.Count == 0)
        {
            ed.WriteMessage("\nThe server offers no operations.");
            return null;
        }
        for (int i = 0; i < listed.Count; i++)
            ed.WriteMessage($"\n  {i + 1}. {listed[i].Id}" + (listed[i].Kind == "recipe" ? "  (recipe)" : ""));

        PromptResult said = ed.GetString(new PromptStringOptions(
            "\nNumber, or names separated by commas: ") { AllowSpaces = true });
        if (said.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(said.StringResult)) return null;
        string typed = said.StringResult.Trim();
        if (int.TryParse(typed, out int number))
        {
            if (number < 1 || number > listed.Count) { ed.WriteMessage("\nNo such number."); return null; }
            return listed[number - 1].Name;
        }
        // As typed: the server cuts it into steps, which may carry answers in
        // brackets with commas of their own.
        return typed;
    }

    /// <summary>Which step of a recipe is asking or refused, where there are several.</summary>
    private static void SayStep(Editor ed, OperationResultDTO result)
    {
        if (result.Step.HasValue && result.Steps > 1)
            ed.WriteMessage($"\nStep {result.Step} of {result.Steps}, {result.Operation}:");
    }

    private static void SayRan(Editor ed, OperationResultDTO result)
    {
        if (result.Ran != null && result.Ran.Count > 1)
            ed.WriteMessage($"\nRan {string.Join(", ", result.Ran)}.");
    }

    private static int? PickNumber(Editor ed, string message, int count, int? byDefault)
    {
        PromptIntegerOptions options = new PromptIntegerOptions(message)
        {
            LowerLimit = 1,
            UpperLimit = count,
        };
        if (byDefault.HasValue) { options.DefaultValue = byDefault.Value; options.UseDefaultValue = true; }
        PromptIntegerResult picked = ed.GetInteger(options);
        return picked.Status == PromptStatus.OK ? picked.Value : null;
    }

    /// <summary>
    /// What to send: a selection, or Enter for the whole drawing. Never the
    /// decoration layers, which the server must not adopt as geometry.
    /// Null when cancelled.
    /// </summary>
    private static ObjectId[]? SelectToSend(Editor ed, Database db)
    {
        PromptSelectionOptions options = new PromptSelectionOptions
        {
            MessageForAdding = "\nSelect what to send, or Enter for the whole drawing: ",
        };
        PromptSelectionResult picked = ed.GetSelection(options);

        IEnumerable<ObjectId> candidates;
        if (picked.Status == PromptStatus.OK)
            candidates = picked.Value.GetObjectIds();
        else if (picked.Status == PromptStatus.Error)
            candidates = AllOfModelspace(db);   // Enter with nothing selected
        else
            return null;

        var decoration = new HashSet<string>(AutoDrawVisualizer.DecorationLayers,
                                             StringComparer.OrdinalIgnoreCase);
        var kept = new List<ObjectId>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in candidates)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && !decoration.Contains(ent.Layer))
                    kept.Add(id);
            }
            tr.Commit();
        }
        return kept.ToArray();
    }

    private static List<ObjectId> AllOfModelspace(Database db)
    {
        var ids = new List<ObjectId>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms) ids.Add(id);
            tr.Commit();
        }
        return ids;
    }

    /// <summary>
    /// Ask each value the operation still needs, with the prompt its type
    /// calls for, into `inputs`. One asked per item goes under its item's
    /// number, which is how the server reads it back. False when cancelled.
    /// </summary>
    private static bool Ask(Editor ed, List<OperationInputDTO> fields, JObject inputs)
    {
        foreach (OperationInputDTO field in fields)
        {
            if (field.Rejected != null && field.Rejected.Type != JTokenType.Null)
                ed.WriteMessage($"\n{field.Rejected} was not accepted.");

            JToken? value = AskOne(ed, field);
            if (value == null) return false;

            if (field.Item.HasValue)
            {
                if (inputs[field.Key] is not JObject perItem)
                {
                    perItem = new JObject();
                    inputs[field.Key] = perItem;
                }
                perItem[field.Item.Value.ToString()] = value;
            }
            else
            {
                inputs[field.Key] = value;
            }
        }
        return true;
    }

    private static JToken? AskOne(Editor ed, OperationInputDTO field)
    {
        string label = "\n" + (string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label);
        bool hasDefault = field.Default != null && field.Default.Type != JTokenType.Null;

        switch (field.Type)
        {
            case "integer":
            {
                var options = new PromptIntegerOptions(label);
                if (field.Min.HasValue) options.LowerLimit = (int)Math.Ceiling(field.Min.Value);
                if (field.Max.HasValue) options.UpperLimit = (int)Math.Floor(field.Max.Value);
                if (hasDefault) { options.DefaultValue = field.Default!.Value<int>(); options.UseDefaultValue = true; }
                PromptIntegerResult said = ed.GetInteger(options);
                return said.Status == PromptStatus.OK ? new JValue(said.Value) : null;
            }
            case "length":
            case "number":
            {
                // A length can be picked off the drawing as well as typed.
                PromptDoubleResult said;
                if (field.Type == "length")
                {
                    var options = new PromptDistanceOptions(label)
                    {
                        AllowNegative = !(field.Min >= 0),
                        AllowZero = !(field.Min > 0),
                    };
                    if (hasDefault) { options.DefaultValue = field.Default!.Value<double>(); options.UseDefaultValue = true; }
                    said = ed.GetDistance(options);
                }
                else
                {
                    var options = new PromptDoubleOptions(label)
                    {
                        AllowNegative = !(field.Min >= 0),
                        AllowZero = !(field.Min > 0),
                    };
                    if (hasDefault) { options.DefaultValue = field.Default!.Value<double>(); options.UseDefaultValue = true; }
                    said = ed.GetDouble(options);
                }
                return said.Status == PromptStatus.OK ? new JValue(said.Value) : null;
            }
            case "choice":
            case "boolean":
            {
                bool yesNo = field.Type == "boolean";
                List<string> choices = yesNo ? new List<string> { "Yes", "No" } : field.Choices ?? new();

                // A keyword is one word of letters and digits, so a choice
                // like a fabric's name is picked from a numbered list instead.
                if (choices.Any(choice => !choice.All(char.IsLetterOrDigit)) || choices.Count > 6)
                {
                    for (int i = 0; i < choices.Count; i++) ed.WriteMessage($"\n  {i + 1}. {choices[i]}");
                    int? defaultAt = hasDefault ? choices.IndexOf(field.Default!.ToString()) + 1 : null;
                    int? number = PickNumber(ed, label, choices.Count, defaultAt > 0 ? defaultAt : null);
                    return number.HasValue ? new JValue(choices[number.Value - 1]) : null;
                }
                var options = new PromptKeywordOptions(label + " [" + string.Join("/", choices) + "]");
                foreach (string choice in choices) options.Keywords.Add(choice);
                if (hasDefault)
                {
                    options.Keywords.Default = yesNo
                        ? (field.Default!.Value<bool>() ? "Yes" : "No")
                        : field.Default!.ToString();
                    options.AllowNone = true;
                }
                PromptResult said = ed.GetKeywords(options);
                if (said.Status != PromptStatus.OK) return null;
                string answer = string.IsNullOrEmpty(said.StringResult)
                    ? options.Keywords.Default
                    : said.StringResult;
                return yesNo ? new JValue(answer == "Yes") : new JValue(answer);
            }
            default:
            {
                var options = new PromptStringOptions(label + ": ") { AllowSpaces = true };
                if (hasDefault) { options.DefaultValue = field.Default!.ToString(); options.UseDefaultValue = true; }
                PromptResult said = ed.GetString(options);
                return said.Status == PromptStatus.OK ? new JValue(said.StringResult) : null;
            }
        }
    }
}
