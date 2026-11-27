using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Operations;
using autodraw_plugin.Services;

using Exception = System.Exception;

namespace autodraw_plugin.Commands;

/// <summary>ADRECIPE: making, changing and saving recipes.</summary>
public class RecipeCommands
{
    /// <summary>
    /// Make a recipe, or change one, by typing its steps: names separated by
    /// commas as ADRUN takes them, each with its answers in brackets where it
    /// has any - `seam_marks, label_panels(height=150)`. An existing recipe is
    /// shown that way first, so editing it is typing the line again; saving
    /// adds its next version and every earlier one stays as it was. `Save`
    /// makes one of the loaded job's last runs instead.
    /// </summary>
    [CommandMethod("ADRECIPE")]
    public async void Edit()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        if (!Prompts.LoggedIn(ed)) return;
        try
        {
            List<OperationDTO> saved = (await OperationService.Catalogue()).Operations
                .Where(listed => listed.Kind == "recipe").ToList();
            foreach (OperationDTO recipe in saved)
                ed.WriteMessage($"\n  {recipe.Id}: {recipe.Written}"
                                + (recipe.Broken != null ? $"  (cannot run: {recipe.Broken})" : ""));

            PromptResult named = ed.GetString(new PromptStringOptions(
                "\nRecipe name, new or from the list, or [Save] the job's last runs as one: ") { AllowSpaces = false });
            if (named.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(named.StringResult)) return;
            string name = named.StringResult.Trim();
            if (name.Equals("s", StringComparison.OrdinalIgnoreCase)
                || name.Equals("save", StringComparison.OrdinalIgnoreCase))
            {
                await SaveLastRuns(ed);
                return;
            }

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

            string? label = Prompts.AskText(ed, "Label", held?.Label);
            if (label == null) return;
            string? description = Prompts.AskText(ed, "Description", held?.Description);
            if (description == null) return;

            SaySaved(ed, await OperationService.SaveRecipe(name, steps, label, description));
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>The loaded job's last runs, in order, as a recipe; what was typed for them its defaults.</summary>
    private static async System.Threading.Tasks.Task SaveLastRuns(Editor ed)
    {
        if (!Job.IsLoaded)
        {
            ed.WriteMessage("\nNo job loaded (ADOPEN, Load): a recipe is saved from a job's runs.");
            return;
        }
        PromptResult named = ed.GetString(new PromptStringOptions("\nRecipe name: ") { AllowSpaces = false });
        if (named.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(named.StringResult)) return;
        int? count = Prompts.PickNumber(ed, "\nHow many of the last runs", 100, 3);
        if (count == null) return;
        SaySaved(ed, await OperationService.SaveRecipe(Job.ProjectId!.Value, named.StringResult, count.Value));
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
}
