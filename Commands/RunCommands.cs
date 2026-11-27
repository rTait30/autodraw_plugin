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
/// ADRUN and ADGO: running the server's operations and recipes, and going on
/// with one that stopped for a person.
///
/// Where a run goes depends only on the open job (ADOPEN):
/// - a job loaded: on the job, its whole drawing sent back, the job answering
///   what it can, the result kept on its history and laid in whole;
/// - a job attached, or none: on what is selected (Enter for the whole
///   drawing), the result replacing exactly that (Layout Replace) or drawn
///   beside it (Stack). An attached job answers what it can and has a line
///   written in its log; with none, everything is asked and nothing kept.
///
/// A recipe stops after a step marked for review, or after every operation
/// (ADOPTIONS Pause Each), and ADGO goes on from there.
/// </summary>
public class RunCommands
{
    [CommandMethod("ADRUN")]
    public async void Run()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        if (!Prompts.LoggedIn(ed)) return;
        try
        {
            Prompts.Choice? chosen = Prompts.ChooseOperation(ed, await OperationService.Catalogue());
            if (chosen == null) { ed.WriteMessage("\nCancelled."); return; }

            // A new run: whatever was waiting is given up.
            Job.Paused = null;
            if (Job.IsLoaded)
                await OnJob(doc, Job.ProjectId!.Value, chosen.Name, chosen.Inputs, chosen.Start);
            else
                await Free(doc, chosen.Name, chosen.Inputs, chosen.Start, null);
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
        JobView.Report(ed);
    }

    /// <summary>
    /// Go on with the run that stopped for a person: the drawing as it is now -
    /// the job's whole drawing, or what is selected for a free run - goes back,
    /// and the recipe runs on from the step after the stop. Refused there, it
    /// still waits at the same place, to be changed and gone on with again.
    /// </summary>
    [CommandMethod("ADGO")]
    public async void Go()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        if (!Prompts.LoggedIn(ed)) return;

        PausedRun? paused = Job.Paused;
        if (paused == null)
        {
            ed.WriteMessage("\nNothing is waiting. " + Job.Status());
            return;
        }
        // Going on is not a run of its own: ADRUN Again stays what ADRUN ran.
        LastRun? last = Job.Last;
        try
        {
            if (paused.ProjectId.HasValue)
            {
                if (!Job.IsLoaded || Job.ProjectId != paused.ProjectId)
                {
                    ed.WriteMessage($"\nThe run waiting is job {paused.ProjectId}'s: ADOPEN {paused.ProjectId} and Load it first.");
                    return;
                }
                await OnJob(doc, paused.ProjectId.Value, paused.Name, paused.Inputs, paused.Next);
            }
            else
            {
                await Free(doc, paused.Name, paused.Inputs, paused.Next, paused.Facts);
            }
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
        Job.Last = last;
        JobView.Report(ed);
    }

    /// <summary>
    /// A free run: what is selected goes up, the result is laid in, and an
    /// attached job is told in its log what ran.
    /// </summary>
    private static async Task Free(Document doc, string name, JObject inputs, int start, JToken? facts)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;
        ObjectId[]? sent = Prompts.SelectToSend(ed, db);
        if (sent == null) { ed.WriteMessage("\nCancelled."); return; }

        string path = Path.Combine(Path.GetTempPath(), $"autodraw_op_{Guid.NewGuid():N}.dxf");
        try
        {
            using (doc.LockDocument())
            {
                // An ObjectIdCollection made from an empty array throws, so
                // nothing is sent as an empty collection.
                OperationService.Export(db, sent.Length > 0 ? new ObjectIdCollection(sent)
                                                            : new ObjectIdCollection(), path);
            }
            ed.WriteMessage(start > 1
                ? $"\nSending {sent.Length} entities to {name} from step {start}..."
                : $"\nSending {sent.Length} entities to {name}...");
            OperationResultDTO? result = await SendAndLayIn(doc, name, sent, path, inputs, start, facts);
            if (result == null || !string.IsNullOrEmpty(result.Error)) return;
            Job.Last = new LastRun { Name = name, Inputs = (JObject)inputs.DeepClone(), Start = start };

            // The drawing is the person's own, so only the log keeps the run.
            if (Job.Mode == JobMode.Attached && Job.ProjectId.HasValue)
            {
                string ran = result.Ran is { Count: > 1 } ? string.Join(", ", result.Ran) : name;
                string said = string.IsNullOrWhiteSpace(result.Notes) ? "" : ": " + result.Notes;
                RunLogDTO log = await OperationService.AddNote(
                    Job.ProjectId.Value, $"[ADRUN] {ran} on {sent.Length} entities{said}", null);
                if (!string.IsNullOrEmpty(log.Error))
                    ed.WriteMessage($"\nNot written in the job's log: {log.Message ?? log.Error}");
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Send what was exported to `path` and lay the result in: answering what
    /// the server asks, selecting each item where it asks that, then replacing
    /// what was sent or drawing beside it. A recipe that stops is held as
    /// `Job.Paused`. Null when cancelled; the reply, refused or not, otherwise.
    /// </summary>
    private static async Task<OperationResultDTO?> SendAndLayIn(
        Document doc, string chosen, ObjectId[] sent, string path, JObject inputs, int start,
        JToken? facts)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;
        Dictionary<int, string>? itemPaths = null;
        try
        {
            // The server keeps nothing between calls, so each answer goes up
            // with the drawing again until it has all it asked for. Where the
            // job has several items and nothing sent says which is which, each
            // item is selected - or left out - and goes up as its own drawing.
            int floor = PieceGroupService.HighestObject(db);
            OperationResultDTO result = await OperationService.Run(chosen, path, inputs, floor,
                                                                   null, start, facts);
            while (true)
            {
                if (result.Error == "needs_input" && result.Inputs != null)
                {
                    Prompts.SayStep(ed, result);
                    if (!Prompts.Ask(ed, result.Inputs, inputs)) { ed.WriteMessage("\nCancelled."); return null; }
                }
                else if (result.Error == "needs_selection" && result.Items != null && itemPaths == null)
                {
                    Prompts.SayStep(ed, result);
                    itemPaths = SelectEachItem(doc, result.Items, out ObjectId[] selected);
                    if (itemPaths == null) { ed.WriteMessage("\nCancelled."); return null; }
                    sent = selected;
                }
                else break;
                result = await OperationService.Run(chosen, path, inputs, floor, itemPaths, start, facts);
            }

            foreach (string line in result.Interpreted ?? new List<string>())
                ed.WriteMessage("\n  " + line);

            if (!string.IsNullOrEmpty(result.Error))
            {
                Prompts.SayStep(ed, result);
                ed.WriteMessage($"\nStopped: {result.Message ?? result.Error}");
                // Going on from a review, the check after it found the change
                // wrong: the run waits where it was, to be changed again.
                return result;
            }
            Prompts.SayRan(ed, result);

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

            Prompts.SayNotes(ed, result);
            ed.WriteMessage(replacing
                ? $"\nReplaced {erased} with {imported}."
                : $"\nDrew {imported} beside what was sent.");
            JobView.Hold(result, chosen, inputs, null);
            return result;
        }
        finally
        {
            foreach (string part in itemPaths?.Values ?? Enumerable.Empty<string>())
            {
                try { File.Delete(part); } catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// Select the geometry of each of the job's items in turn, Enter to leave an
    /// item out, each exported to a DXF of its own. Null when cancelled or when
    /// nothing at all was selected. `picked` is everything selected, which the
    /// result replaces.
    /// </summary>
    private static Dictionary<int, string>? SelectEachItem(Document doc,
                                                           List<SelectionItemDTO> items,
                                                           out ObjectId[] picked)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;
        var paths = new Dictionary<int, string>();
        var all = new List<ObjectId>();
        picked = Array.Empty<ObjectId>();
        foreach (SelectionItemDTO item in items)
        {
            PromptSelectionResult chosen = ed.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = $"\nSelect {item.Label ?? $"item {item.Item + 1}"}, or Enter to leave it out: ",
            });
            if (chosen.Status == PromptStatus.Error) continue;   // Enter: left out
            if (chosen.Status != PromptStatus.OK)
            {
                foreach (string made in paths.Values) { try { File.Delete(made); } catch (IOException) { } }
                return null;
            }

            ObjectId[] kept = Prompts.WithoutDecoration(db, chosen.Value.GetObjectIds());
            if (kept.Length == 0) continue;

            string path = Path.Combine(Path.GetTempPath(), $"autodraw_item_{Guid.NewGuid():N}.dxf");
            using (doc.LockDocument())
            {
                OperationService.Export(db, new ObjectIdCollection(kept), path);
            }
            paths[item.Item] = path;
            all.AddRange(kept);
        }
        if (paths.Count == 0)
        {
            ed.WriteMessage("\nNothing was selected for any item.");
            return null;
        }
        picked = all.ToArray();
        return paths;
    }

    /// <summary>
    /// Run on the loaded job from step `start`: its whole drawing goes back,
    /// the job answers what it can and anything else is asked, and the job's
    /// new drawing replaces this one whole. What ran is kept on its history
    /// even where the recipe stops part way, held then as `Job.Paused`.
    /// </summary>
    private static async Task OnJob(Document doc, int projectId, string chosen, JObject inputs, int start)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;
        string path = Path.Combine(Path.GetTempPath(), $"autodraw_op_{Guid.NewGuid():N}.dxf");
        try
        {
            ObjectId[] everything = Prompts.WithoutDecoration(db, Prompts.AllOfModelspace(db));
            using (doc.LockDocument())
            {
                // An ObjectIdCollection made from an empty array throws - a
                // job with nothing drawn yet lays in nothing.
                OperationService.Export(db, everything.Length > 0 ? new ObjectIdCollection(everything)
                                                                  : new ObjectIdCollection(), path);
            }
            ed.WriteMessage(start > 1
                ? $"\nGoing on with {chosen} on job {projectId} from step {start}..."
                : $"\nRunning {chosen} on job {projectId}...");

            OperationResultDTO result = await OperationService.RunOnProject(
                projectId, chosen, path, inputs, Job.CurrentArtifact, start);
            while (result.Error == "needs_input" && result.Inputs != null)
            {
                // The drawing is kept with the first call; answers go without it.
                Job.CurrentArtifact = result.ArtifactId;
                Prompts.SayStep(ed, result);
                if (!Prompts.Ask(ed, result.Inputs, inputs)) { ed.WriteMessage("\nCancelled."); return; }
                result = await OperationService.RunOnProject(
                    projectId, chosen, null, inputs, Job.CurrentArtifact, start);
            }

            foreach (string line in result.Interpreted ?? new List<string>())
                ed.WriteMessage("\n  " + line);
            if (!string.IsNullOrEmpty(result.Error))
            {
                Prompts.SayStep(ed, result);
                ed.WriteMessage($"\nStopped: {result.Message ?? result.Error}");
                if (result.Error == "stale")
                    ed.WriteMessage($"\nThe job moved on elsewhere: ADOPEN {projectId} to lay it in again.");
                // Refused going on from a stop: the drawing sent is kept, and
                // the run still waits there.
                else if (result.ArtifactId.HasValue)
                    Job.CurrentArtifact = result.ArtifactId;
                return;
            }

            JobView.LayIn(doc, result);
            Prompts.SayRan(ed, result);
            Prompts.SayNotes(ed, result);
            JobView.Hold(result, chosen, inputs, projectId);
            Job.Last = new LastRun { Name = chosen, Inputs = (JObject)inputs.DeepClone(), Start = start };
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
