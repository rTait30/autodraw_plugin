using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Operations;
using autodraw_plugin.Services;

namespace autodraw_plugin.Commands;

/// <summary>
/// Asking the designer, and choosing what to send: the prompts every command
/// shares. What a value means is the operation's business - it says what it
/// needs and of what type, and this only picks the prompt to ask it with.
/// </summary>
internal static class Prompts
{
    public static bool LoggedIn(Editor ed)
    {
        if (autodraw.Auth.IsLoggedIn) return true;
        ed.WriteMessage("\nPlease login first (ADLOGIN).");
        return false;
    }

    /// <summary>What to run: a name or names, from which step, with which answers already given.</summary>
    public record Choice(string Name, int Start, JObject Inputs);

    /// <summary>
    /// An operation or recipe by its number in the list, or names typed in -
    /// several separated by commas run in turn, as a recipe saved nowhere,
    /// each with answers in brackets where it is given any. `Again` is the
    /// last run with the answers it was given; `Part` a recipe from a step
    /// picked. Null when cancelled.
    /// </summary>
    public static Choice? ChooseOperation(Editor ed, OperationCatalogueDTO catalogue)
    {
        var listed = catalogue.Operations;
        if (listed.Count == 0)
        {
            ed.WriteMessage("\nThe server offers no operations.");
            return null;
        }
        for (int i = 0; i < listed.Count; i++)
            ed.WriteMessage($"\n  {i + 1}. {listed[i].Id}" + (listed[i].Kind == "recipe" ? "  (recipe)" : ""));

        string again = Job.Last != null ? $" <{Job.Last.Name}>" : "";
        PromptResult said = ed.GetString(new PromptStringOptions(
            $"\nNumber, names separated by commas, or [Again/Part]{again}: ") { AllowSpaces = true });
        if (said.Status != PromptStatus.OK) return null;
        string typed = said.StringResult.Trim();

        // Enter, or Again: the last run as it was, answers and all.
        if (typed.Length == 0 || typed.Equals("a", StringComparison.OrdinalIgnoreCase)
                              || typed.Equals("again", StringComparison.OrdinalIgnoreCase))
        {
            if (Job.Last == null) { ed.WriteMessage("\nNothing has been run yet."); return null; }
            return new Choice(Job.Last.Name, Job.Last.Start, (JObject)Job.Last.Inputs.DeepClone());
        }
        if (typed.Equals("p", StringComparison.OrdinalIgnoreCase)
            || typed.Equals("part", StringComparison.OrdinalIgnoreCase))
            return ChoosePart(ed, catalogue);
        if (int.TryParse(typed, out int number))
        {
            if (number < 1 || number > listed.Count) { ed.WriteMessage("\nNo such number."); return null; }
            return new Choice(listed[number - 1].Name, 1, new JObject());
        }
        // As typed: the server cuts it into steps, which may carry answers in
        // brackets with commas of their own.
        return new Choice(typed, 1, new JObject());
    }

    /// <summary>A recipe from the recipes alone, then the step to start at from its operations as they run.</summary>
    private static Choice? ChoosePart(Editor ed, OperationCatalogueDTO catalogue)
    {
        List<OperationDTO> recipes = catalogue.Operations
            .Where(o => o.Kind == "recipe" && o.Broken == null && o.Operations.Count > 0)
            .ToList();
        if (recipes.Count == 0) { ed.WriteMessage("\nThe server has no recipes."); return null; }
        for (int i = 0; i < recipes.Count; i++)
            ed.WriteMessage($"\n  {i + 1}. {recipes[i].Id}");
        int? which = PickNumber(ed, "\nWhich recipe: ", recipes.Count, null);
        if (which == null) return null;
        OperationDTO recipe = recipes[which.Value - 1];

        for (int i = 0; i < recipe.Operations.Count; i++)
            ed.WriteMessage($"\n  {i + 1}. {recipe.Operations[i]}");
        int? start = PickNumber(ed, "\nStart at step: ", recipe.Operations.Count, null);
        return start == null ? null : new Choice(recipe.Name, start.Value, new JObject());
    }

    /// <summary>Which step of a recipe is asking or refused, where there are several.</summary>
    public static void SayStep(Editor ed, OperationResultDTO result)
    {
        if (result.Step.HasValue && result.Steps > 1)
            ed.WriteMessage($"\nStep {result.Step} of {result.Steps}, {result.Operation}:");
    }

    public static void SayRan(Editor ed, OperationResultDTO result)
    {
        if (result.Ran != null && result.Ran.Count > 1)
            ed.WriteMessage($"\nRan {string.Join(", ", result.Ran)}.");
    }

    /// <summary>What a run said: its notes and warnings, as they are kept in the job's log.</summary>
    public static void SayNotes(Editor ed, OperationResultDTO result)
    {
        if (!string.IsNullOrWhiteSpace(result.Notes)) ed.WriteMessage("\n" + result.Notes);
        foreach (string warning in result.Warnings ?? new List<string>())
            ed.WriteMessage("\nWarning: " + warning);
    }

    public static int? PickNumber(Editor ed, string message, int count, int? byDefault)
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

    /// <summary>A line of text, Enter to keep what is there; null when cancelled.</summary>
    public static string? AskText(Editor ed, string what, string? now)
    {
        PromptResult said = ed.GetString(new PromptStringOptions(string.IsNullOrEmpty(now)
            ? $"\n{what} (Enter for none): "
            : $"\n{what} <{now}> (Enter to keep): ") { AllowSpaces = true });
        return said.Status == PromptStatus.OK ? said.StringResult.Trim() : null;
    }

    /// <summary>One of a few words, the one in force by default; null when cancelled.</summary>
    public static string? AskKeyword(Editor ed, string message, string now, params string[] words)
    {
        var options = new PromptKeywordOptions(message);
        foreach (string word in words) options.Keywords.Add(word);
        options.Keywords.Default = now;
        PromptResult said = ed.GetKeywords(options);
        return said.Status == PromptStatus.OK ? said.StringResult : null;
    }

    /// <summary>
    /// What to send: a selection, or Enter for the whole drawing. Never the
    /// decoration layers, which the server must not adopt as geometry.
    /// Null when cancelled.
    /// </summary>
    public static ObjectId[]? SelectToSend(Editor ed, Database db)
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
        return WithoutDecoration(db, candidates);
    }

    public static ObjectId[] WithoutDecoration(Database db, IEnumerable<ObjectId> candidates)
    {
        var decoration = new HashSet<string>(AutoDrawVisualizer.DecorationLayers,
                                             StringComparer.OrdinalIgnoreCase);
        var kept = new List<ObjectId>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            foreach (ObjectId id in candidates)
            {
                // Nor the pictures of earlier states, which are on the layers
                // they were drawn on: known by what they are, not where.
                if (tr.GetObject(id, OpenMode.ForRead) is Entity ent && !decoration.Contains(ent.Layer)
                    && !OperationGridService.IsPicture(ent))
                    kept.Add(id);
            }
            tr.Commit();
        }
        return kept.ToArray();
    }

    public static List<ObjectId> AllOfModelspace(Database db)
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
    /// A server timestamp as local time, briefly; nothing when there is none.
    /// The server writes UTC without saying so, hence assumed.
    /// </summary>
    public static string When(string? iso) =>
        DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime at)
            ? $" [{at:d MMM HH:mm}]"
            : "";

    /// <summary>
    /// Ask each value the operation still needs, with the prompt its type
    /// calls for, into `inputs`. One asked per item goes under its item's
    /// number, which is how the server reads it back. False when cancelled.
    /// </summary>
    public static bool Ask(Editor ed, List<OperationInputDTO> fields, JObject inputs)
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
