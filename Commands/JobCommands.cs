using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using autodraw_plugin.Models.Operations;
using autodraw_plugin.Services;

using Exception = System.Exception;

namespace autodraw_plugin.Commands;

/// <summary>
/// The open job: opening and closing it, where it stands, moving along its
/// history, its log, and the options every run follows.
/// </summary>
public class JobCommands
{
    /// <summary>
    /// Open a job by its number, or Enter to close the one open.
    ///
    /// Load lays the job's own drawing in, replacing the drawing - for a job
    /// the server draws, whose history ADBACK, ADFORWARD and ADGO then work
    /// along, and which says if a run waits on a person. Attach leaves the
    /// drawing as it is: the job only answers what runs ask and keeps the log
    /// - for a job drawn by hand that wants an operation here and there.
    /// </summary>
    [CommandMethod("ADOPEN")]
    public async void Open()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        if (!Prompts.LoggedIn(ed)) return;

        PromptResult said = ed.GetString(new PromptStringOptions(Job.IsOpen
            ? $"\nJob number, or Enter to close job {Job.ProjectId}: "
            : "\nJob number: ") { AllowSpaces = false });
        if (said.Status != PromptStatus.OK) return;
        string typed = said.StringResult.Trim();
        if (typed.Length == 0)
        {
            if (Job.IsOpen) ed.WriteMessage($"\nJob {Job.ProjectId} closed; the drawing stays as it is.");
            Job.Close();
            JobView.Report(ed);
            return;
        }
        if (!int.TryParse(typed, out int projectId))
        {
            ed.WriteMessage("\nNot a job number.");
            return;
        }

        // Loading replaces the drawing, so a drawing with work in it is
        // attached unless the person says otherwise.
        bool drawn = Prompts.WithoutDecoration(doc.Database, Prompts.AllOfModelspace(doc.Database)).Length > 0;
        string? how = Prompts.AskKeyword(ed, drawn
            ? "\n[Load] the job's drawing in place of this one, or [Attach] it to your drawing as it is"
            : "\n[Load] the job's drawing, or [Attach] it to your drawing", drawn ? "Attach" : "Load",
            "Load", "Attach");
        if (how == null) return;

        try
        {
            if (how == "Load")
            {
                OperationResultDTO laid = await OperationService.ProjectDrawing(projectId);
                if (!string.IsNullOrEmpty(laid.Error)) { ed.WriteMessage($"\n{laid.Message ?? laid.Error}"); return; }
                Job.Open(projectId, JobMode.Loaded);
                JobView.LayIn(doc, laid);
            }
            else
            {
                // Asked for the log only to know the job is there.
                RunLogDTO log = await OperationService.Log(projectId);
                if (!string.IsNullOrEmpty(log.Error)) { ed.WriteMessage($"\n{log.Message ?? log.Error}"); return; }
                Job.Open(projectId, JobMode.Attached);
            }
        }
        catch (System.Net.Http.HttpRequestException ex)
            when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            ed.WriteMessage($"\nThere is no job {projectId}.");
            return;
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
            return;
        }
        JobView.Report(ed);
    }

    /// <summary>Who is logged in, the job and where it stands, and the options in force.</summary>
    [CommandMethod("ADSTATUS")]
    public void Status()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        PluginSettings settings = PluginSettings.Current;
        ed.WriteMessage(autodraw.Auth.IsLoggedIn
            ? $"\nLogged in as {autodraw.Auth.CurrentUser} ({autodraw.Auth.Role})."
            : "\nNot logged in (ADLOGIN).");
        ed.WriteMessage(Job.Mode switch
        {
            JobMode.Loaded => "\nThe job's drawing is laid in: runs are kept on its history.",
            JobMode.Attached => "\nThe drawing is yours: runs work on what you select, the job answers and keeps the log.",
            _ => "\nNo job: runs work on what you select and ask everything.",
        });
        ed.WriteMessage($"\nOptions: recipes stop {(settings.StepThrough ? "after every operation" : "at reviews")}, "
                        + $"layout {Options.LayoutOf(settings)} {settings.Direction}, "
                        + $"depth {(settings.StepsDepth == 0 ? "off" : settings.StepsDepth < 0 ? "all" : settings.StepsDepth.ToString())}.");
        if (Job.Last != null) ed.WriteMessage($"\nLast run: {Job.Last.Name} (ADRUN Enter to run it again).");
        JobView.Report(ed);
    }

    [CommandMethod("ADBACK")]
    public async void Back() => await Along(false);

    [CommandMethod("ADFORWARD")]
    public async void Forward() => await Along(true);

    /// <summary>Back along the loaded job's history, or forward again - an operation at a time.</summary>
    private static async Task Along(bool forward)
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        if (!Job.IsLoaded)
        {
            ed.WriteMessage("\nNo job loaded: ADOPEN a job and Load it to move along its history.");
            return;
        }
        try
        {
            OperationResultDTO moved = await OperationService.Step(Job.ProjectId!.Value, forward);
            if (!string.IsNullOrEmpty(moved.Error)) ed.WriteMessage($"\n{moved.Message ?? moved.Error}");
            if (moved.Drawing != null) JobView.LayIn(doc, moved);
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
        JobView.Report(ed);
    }

    /// <summary>
    /// Write a note in the job's log, beside what the runs said: for one item
    /// or the whole job, against the drawing the job is at.
    /// </summary>
    [CommandMethod("ADNOTE")]
    public async void Note()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        if (!Prompts.LoggedIn(ed)) return;
        if (!Job.IsOpen)
        {
            ed.WriteMessage("\nNo job open (ADOPEN): a note goes in a job's log.");
            return;
        }
        PromptResult typed = ed.GetString(new PromptStringOptions("\nNote: ") { AllowSpaces = true });
        if (typed.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(typed.StringResult)) return;

        // The items the history has drawn are offered; any item's id is taken.
        List<int> items = Job.Items;
        var which = new PromptIntegerOptions(items.Count > 0
            ? $"\nFor item ({string.Join(", ", items)}), or Enter for the whole job: "
            : "\nFor item id, or Enter for the whole job: ")
        {
            AllowNone = true,
            AllowNegative = false,
            AllowZero = false,
        };
        PromptIntegerResult item = ed.GetInteger(which);
        if (item.Status != PromptStatus.OK && item.Status != PromptStatus.None) return;
        int? itemId = item.Status == PromptStatus.OK ? item.Value : null;

        try
        {
            RunLogDTO log = await OperationService.AddNote(Job.ProjectId!.Value, typed.StringResult.Trim(), itemId);
            if (!string.IsNullOrEmpty(log.Error)) { ed.WriteMessage($"\nNot written: {log.Message ?? log.Error}"); return; }
            ed.WriteMessage(itemId.HasValue ? $"\nNoted for item {itemId}." : "\nNoted for the job.");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// The job's log: each operation along its history, oldest first, with
    /// what it said when it ran and what people wrote.
    /// </summary>
    [CommandMethod("ADLOG")]
    public async void Log()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        if (!Prompts.LoggedIn(ed)) return;
        if (!Job.IsOpen)
        {
            ed.WriteMessage("\nNo job open (ADOPEN).");
            return;
        }
        try
        {
            RunLogDTO log = await OperationService.Log(Job.ProjectId!.Value);
            if (!string.IsNullOrEmpty(log.Error)) { ed.WriteMessage($"\n{log.Message ?? log.Error}"); return; }
            if (log.Log.Count == 0) { ed.WriteMessage("\nNothing in the log yet."); return; }
            ed.WriteMessage($"\nJob {Job.ProjectId}'s log:");
            int number = 0;
            foreach (RunLogEntryDTO entry in log.Log)
            {
                if (entry.ArtifactId == null && entry.Operation == null)
                    ed.WriteMessage("\n  Notes:");
                else
                {
                    number++;
                    string what = entry.Operation ?? (entry.Source == "submitted" ? "changed in CAD" : entry.Source ?? "?");
                    string recipe = entry.Recipe != null ? $" ({entry.Recipe})" : "";
                    string items = entry.Items is { Count: > 0 } ? $", items {string.Join(", ", entry.Items)}" : "";
                    ed.WriteMessage($"\n  {number}. {what}{recipe}{items}{Prompts.When(entry.CreatedAt)}");
                }
                if (!string.IsNullOrWhiteSpace(entry.Notes))
                    foreach (string line in entry.Notes.Split('\n'))
                        ed.WriteMessage("\n       " + line.TrimEnd());
                foreach (RunNoteDTO note in entry.Written ?? new List<RunNoteDTO>())
                {
                    string forItem = note.ItemId.HasValue ? $" on item {note.ItemId}" : "";
                    ed.WriteMessage($"\n       > {note.User ?? "someone"}{forItem}{Prompts.When(note.CreatedAt)}: {note.Text}");
                }
            }
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }
}

/// <summary>ADOPTIONS: how every run goes and how a job's drawing is laid out.</summary>
public class Options
{
    /// <summary>
    /// Pause: a recipe stops after every operation (Each) or only at a step
    /// marked for review (Reviews). Layout: each state drawn over the last
    /// (Replace), kept beside it (Stack), or a loaded job's history as a grid
    /// of operations by items (Grid). Direction: which way states run. Depth:
    /// how far into a free run of several steps its states are drawn, in Stack.
    /// </summary>
    [CommandMethod("ADOPTIONS")]
    public void Change()
    {
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;
        PluginSettings settings = PluginSettings.Current;
        string laidOutAs = LayoutOf(settings) + "/" + settings.Direction;

        while (true)
        {
            string depth = settings.StepsDepth == 0 ? "Off" : settings.StepsDepth < 0 ? "All" : settings.StepsDepth.ToString();
            var options = new PromptKeywordOptions(
                $"\nPause {(settings.StepThrough ? "Each" : "Reviews")}, layout {LayoutOf(settings)}, "
                + $"direction {settings.Direction}, depth {depth}. Change [Pause/Layout/Direction/Depth] <done>: ");
            options.Keywords.Add("Pause");
            options.Keywords.Add("Layout");
            options.Keywords.Add("Direction");
            options.Keywords.Add("Depth");
            options.AllowNone = true;
            PromptResult picked = ed.GetKeywords(options);
            if (picked.Status != PromptStatus.OK || string.IsNullOrEmpty(picked.StringResult)) break;

            switch (picked.StringResult)
            {
                case "Pause":
                {
                    string? said = Prompts.AskKeyword(ed,
                        "\nA recipe stops [Each] after every operation, or only at [Reviews]",
                        settings.StepThrough ? "Each" : "Reviews", "Each", "Reviews");
                    if (said == null) continue;
                    settings.StepThrough = said == "Each";
                    break;
                }
                case "Layout":
                {
                    string? said = Prompts.AskKeyword(ed,
                        "\nEach state [Replace] over the last, [Stack] beside it, or a job's history as a [Grid] of operations by items",
                        LayoutOf(settings), "Replace", "Stack", "Grid");
                    if (said == null) continue;
                    // Grid is for a job's history only; free runs keep Replace or Stack.
                    settings.OperationsGrid = said == "Grid";
                    if (!settings.OperationsGrid) settings.Layout = said;
                    break;
                }
                case "Direction":
                {
                    string? said = Prompts.AskKeyword(ed, "\nStates run [Right/Down]", settings.Direction, "Right", "Down");
                    if (said == null) continue;
                    settings.Direction = said;
                    break;
                }
                default:
                {
                    var asked = new PromptIntegerOptions(
                        "\nIn Stack, draw each state of a free run of several steps down to depth (0 off, 1 each step, 2 into its recipes) or [All]")
                    {
                        AllowNegative = false,
                        AllowNone = true,
                        DefaultValue = Math.Max(settings.StepsDepth, 0),
                        UseDefaultValue = settings.StepsDepth >= 0,
                    };
                    asked.Keywords.Add("All");
                    PromptIntegerResult said = ed.GetInteger(asked);
                    if (said.Status == PromptStatus.Keyword) settings.StepsDepth = -1;
                    else if (said.Status == PromptStatus.OK) settings.StepsDepth = said.Value;
                    else continue;
                    break;
                }
            }
            settings.Save();
        }

        // A loaded job is laid out as asked for when it is laid in, so a change
        // is seen by laying it in again - not done here, as that would drop
        // anything changed by hand and not yet sent.
        if (LayoutOf(settings) + "/" + settings.Direction != laidOutAs && Job.IsLoaded)
            ed.WriteMessage($"\nADOPEN {Job.ProjectId} and Load it to see it laid out as set.");
    }

    public static string LayoutOf(PluginSettings settings) =>
        settings.OperationsGrid ? "Grid" : settings.Layout;
}
