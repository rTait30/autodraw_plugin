using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using autodraw_plugin.Services;
using System;
using System.IO;
using autodraw_plugin.Models.AutoDraw;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using Exception = System.Exception;

namespace autodraw_plugin.Commands;

public class AutoDrawCommands
{
    private static int? PromptForProjectId(Editor ed, string message)
    {
        PromptStringOptions options = new PromptStringOptions(message) { AllowSpaces = false };
        PromptResult result = ed.GetString(options);
        if (result.Status != PromptStatus.OK) return null;

        if (!int.TryParse(result.StringResult, out int projectId))
        {
            ed.WriteMessage("\nInvalid Project ID.");
            return null;
        }
        return projectId;
    }

    private static bool Confirm(Editor ed, string message)
    {
        PromptKeywordOptions options = new PromptKeywordOptions(message);
        options.Keywords.Add("Yes");
        options.Keywords.Add("No");
        options.Keywords.Default = "No";
        options.AllowNone = true;

        PromptResult result = ed.GetKeywords(options);
        return result.Status == PromptStatus.OK && result.StringResult == "Yes";
    }

    /// <summary>Entities a resync would replace - everything but the INFO board.</summary>
    private static int CountReplaceable(Database db)
    {
        int count = 0;
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
            {
                Entity ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (ent != null && ent.Layer != AutoDrawVisualizer.InfoLayer
                    && ent.Layer != AutoDrawVisualizer.NotesLayer) count++;
            }
            tr.Commit();
        }
        return count;
    }

    /// <summary>
    /// After a logout or a restart the session has no project, but the drawing
    /// still holds the work. Ask rather than forcing an ADSTART, which would
    /// redraw everything from the server.
    /// </summary>
    private static async Task<bool> EnsureProject(Editor ed)
    {
        if (autodraw.AutoDraw.HasActiveProject) return true;

        int? picked = PromptForProjectId(ed, "\nNo active project. Enter Project ID to resume: ");
        if (picked == null) return false;

        ed.WriteMessage($"\nFetching project {picked.Value}...");
        await autodraw.AutoDraw.StartProject(picked.Value);

        if (!autodraw.AutoDraw.HasActiveProject)
        {
            ed.WriteMessage("\nFailed to load project data. Check API or ID.");
            return false;
        }
        return true;
    }

    [CommandMethod("ADSTART")]
    public async void StartAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn) 
        { 
             ed.WriteMessage("\nPlease login first (ADLOGIN)."); 
             return; 
        }

        try
        {
             int? picked = PromptForProjectId(ed, "\nEnter Project ID: ");
             if (picked == null) return;
             int projectId = picked.Value;

             ed.WriteMessage($"\nFetching project {projectId}...");

             await autodraw.AutoDraw.StartProject(projectId);

             if (!autodraw.AutoDraw.HasActiveProject)
             {
                 ed.WriteMessage("\nFailed to load project data. Check API or ID.");
                 return;
             }

             var data = autodraw.AutoDraw.CurrentProjectData!;

             // ADSTART resyncs the drawing to the record, which means wiping
             // what is there. Harmless on an empty drawing - the usual case -
             // but confirm first if there is anything to lose.
             int existing = CountReplaceable(db);
             if (existing > 0 && !Confirm(ed,
                     $"\nThis will replace {existing} entities with the server's copy. Continue?"))
             {
                 ed.WriteMessage("\nCancelled.");
                 return;
             }

             var (erased, imported) = await LayInFetched(doc, data);
             ed.WriteMessage($"\nProject {data.ProjectId} - {data.ProjectName}");
             ed.WriteMessage($"\nErased {erased} server entities, drew {imported}.");
             ed.WriteMessage($"\nSubmitting layers: {string.Join(", ", data.Submission.Layers)}");
             ed.WriteMessage("\nAutoDraw setup complete.");
        }
        catch (Exception ex)
        {
             ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch the job again and lay the whole of it in, with no questions. What
    /// a change of layout does: the server does the placing, so seeing the job
    /// in another layout means asking for it in that one.
    /// </summary>
    private static async Task LayIn(Document doc, int projectId)
    {
        Editor ed = doc.Editor;
        await autodraw.AutoDraw.StartProject(projectId);
        if (!autodraw.AutoDraw.HasActiveProject)
        {
            ed.WriteMessage("\nFailed to load project data. Check API or ID.");
            return;
        }
        var (erased, imported) = await LayInFetched(doc, autodraw.AutoDraw.CurrentProjectData!);
        ed.WriteMessage($"\nLaid out again: erased {erased}, drew {imported}.");
    }

    /// <summary>
    /// Replace the drawing with the job as just fetched: the server's full
    /// copy, the board, the notes, and the grid around it.
    /// </summary>
    private static async Task<(int erased, int imported)> LayInFetched(
        Document doc, autodraw_plugin.Models.Projects.ProjectDetailsDTO data)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;
        int erased = 0, imported = 0;

        using (DocumentLock docLock = doc.LockDocument())
        {
            // A full resync: everything except the INFO board goes, and the
            // server's full-scope copy replaces it - MPanel's geometry
            // included, so a half-done job comes back complete.
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                erased = DxfTransferService.EraseAllExcept(
                    tr, db, AutoDrawVisualizer.DecorationLayers);
                tr.Commit();
            }

            imported = DxfTransferService.ImportBase64(db, data.Dxf);

            // Then the board, clear of whatever was just drawn.
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Cloning the server's entities in gave them new handles,
                // so the groups it sent refer to nothing. Rebuild them.
                RebuildDerived(tr, db, data.AutodrawRecord);

                AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                AutoDrawVisualizer.ClearInfoLayer(tr, db);

                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                AutoDrawVisualizer.DrawStatusBoard(tr, btr, data.AutodrawConfig, data.AutodrawMeta, origin,
                    autodraw.AutoDraw.CurrentBranch);
                RedrawNotes(tr, db, btr, data.Notes);
                GridService.DrawLabels(tr, db, btr, data.Grid);

                tr.Commit();
            }
        }

        await GridService.SyncSnapshots(doc, data.ProjectId, data.Grid,
            data.AutodrawRecord, data.AutodrawConfig);
        ed.Regen();
        return (erased, imported);
    }

    /// <summary>
    /// Ask for the values a step said it needs. The step declares them, so the
    /// plugin needs no knowledge of what any particular step wants.
    /// </summary>
    private static Dictionary<string, string> AskFor(Editor ed, List<InputRequestDTO> fields)
    {
        var answers = new Dictionary<string, string>();
        foreach (InputRequestDTO field in fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Help)) ed.WriteMessage("\n" + field.Help);

            string label = string.IsNullOrWhiteSpace(field.Label) ? field.Key : field.Label;
            string suffix = string.IsNullOrWhiteSpace(field.Default) ? "" : " <" + field.Default + ">";

            PromptStringOptions options = new PromptStringOptions("\n" + label + suffix + ": ")
            {
                AllowSpaces = true,
            };
            PromptResult result = ed.GetString(options);
            if (result.Status != PromptStatus.OK) return null;

            string value = string.IsNullOrWhiteSpace(result.StringResult)
                ? field.Default
                : result.StringResult.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                ed.WriteMessage("\n" + label + " is required.");
                return null;
            }
            answers[field.Key] = value;
        }
        return answers;
    }

    /// <summary>
    /// Redraw the notes panel from what the server just sent. Like the status
    /// board it is rebuilt every time rather than kept in the drawing, so it
    /// always describes the branch now loaded.
    /// </summary>
    private static void RedrawNotes(Transaction tr, Database db, BlockTableRecord btr, List<NoteDTO> notes)
    {
        AutoDrawVisualizer.EnsureLayer(tr, db, AutoDrawVisualizer.NotesLayer);
        AutoDrawVisualizer.ClearLayer(tr, db, AutoDrawVisualizer.NotesLayer);
        AutoDrawVisualizer.DrawNotes(tr, btr, notes, AutoDrawVisualizer.NotesOrigin());
    }

    /// <summary>
    /// Ask which substep to go to. Empty means one step, as it always did.
    ///
    /// A named substep is resolved against the config and read back for
    /// confirmation, because moving eight steps by mistyping one is a poor way
    /// to find out the key was wrong. A substep can also be given by position -
    /// `3.1` is step three substep one, the numbers the board prints - which is
    /// resolved here so the confirmation names it the same way either way.
    /// </summary>
    private static bool AskWhichSubstep(Editor ed, string verb, string andAfter,
                                        out string toSubstep, string enterMeans = "one step")
    {
        toSubstep = null;

        PromptStringOptions options = new PromptStringOptions(
            "\n" + verb + " to which substep, or Enter for " + enterMeans + ": ")
        {
            AllowSpaces = false,
        };
        PromptResult answer = ed.GetString(options);
        if (answer.Status != PromptStatus.OK) return false;

        string wanted = answer.StringResult?.Trim();
        if (string.IsNullOrWhiteSpace(wanted)) return true;      // one step

        var config = autodraw.AutoDraw.CurrentProjectData?.AutodrawConfig;
        if (config == null) { ed.WriteMessage("\nNo project loaded."); return false; }

        for (int i = 0; i < config.Steps.Count; i++)
        {
            ConfigStepDTO step = config.Steps[i];
            for (int j = 0; j < step.Substeps.Count; j++)
            {
                ConfigSubstepDTO substep = step.Substeps[j];
                bool hit = string.Equals(substep.Key, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(step.Key + "." + substep.Key, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(i + "." + j, wanted, StringComparison.Ordinal)
                    || (j == 0 && string.Equals(i.ToString(), wanted, StringComparison.Ordinal));
                if (!hit) continue;

                PromptKeywordOptions confirm = new PromptKeywordOptions(
                    "\n" + verb + " to " + step.Label + " / " + substep.Label
                    + " (" + step.Key + "." + substep.Key + ")" + andAfter + "? ");
                confirm.Keywords.Add("Yes");
                confirm.Keywords.Add("No");
                confirm.Keywords.Default = "Yes";
                PromptResult said = ed.GetKeywords(confirm);
                if (said.Status != PromptStatus.OK || said.StringResult != "Yes") return false;

                toSubstep = step.Key + "." + substep.Key;
                return true;
            }
        }

        ed.WriteMessage("\nNo substep called '" + wanted + "'. Known keys:");
        for (int i = 0; i < config.Steps.Count; i++)
        {
            ConfigStepDTO step = config.Steps[i];
            for (int j = 0; j < step.Substeps.Count; j++)
            {
                ed.WriteMessage("\n  " + i + "." + j + "  " + step.Key + "."
                                + step.Substeps[j].Key + "  - " + step.Substeps[j].Label);
            }
        }
        return false;
    }

    /// <summary>
    /// Ask what to call the line about to be started. Only reached when the
    /// coming step has already been run from this exact state, so the question
    /// is which of two lines you mean - not whether to overwrite anything.
    /// </summary>
    private static string AskForBranch(Editor ed, ContinueResponseDTO result)
    {
        ed.WriteMessage("\n" + result.Message);

        string suffix = string.IsNullOrWhiteSpace(result.Suggested) ? "" : " <" + result.Suggested + ">";
        PromptStringOptions options = new PromptStringOptions("\nName for this branch" + suffix + ": ")
        {
            AllowSpaces = false,
        };
        PromptResult answer = ed.GetString(options);
        if (answer.Status != PromptStatus.OK) return null;

        string value = answer.StringResult?.Trim();
        return string.IsNullOrWhiteSpace(value) ? result.Suggested : value;
    }

    /// <summary>
    /// When the coming substep offers a choice, let the designer make it rather
    /// than silently taking the configured default.
    /// </summary>
    private static string ChooseOption(Editor ed)
    {
        var data = autodraw.AutoDraw.CurrentProjectData;
        var meta = data?.AutodrawMeta;
        var steps = data?.AutodrawConfig?.Steps;
        if (steps == null || meta == null || meta.CurrentStep >= steps.Count) return null;

        var substeps = steps[meta.CurrentStep].Substeps;
        if (substeps == null || meta.CurrentSubstep >= substeps.Count) return null;

        var substep = substeps[meta.CurrentSubstep];
        var choices = substep.Options?.Where(o => o.Automated).ToList();
        if (choices == null || choices.Count < 2) return null;

        ed.WriteMessage("\n" + substep.Label + ":");
        for (int i = 0; i < choices.Count; i++)
        {
            string mark = choices[i].IsDefault ? "  (default)" : "";
            ed.WriteMessage("\n  " + (i + 1) + ". " + choices[i].Label + mark);
        }

        PromptIntegerOptions options = new PromptIntegerOptions("\nChoose")
        {
            LowerLimit = 1,
            UpperLimit = choices.Count,
            AllowNone = true,
        };
        PromptIntegerResult result = ed.GetInteger(options);
        if (result.Status != PromptStatus.OK) return null;   // fall back to the default
        return choices[result.Value - 1].Key;
    }

    [CommandMethod("ADSTATUS")]
    public async void StatusAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        ed.WriteMessage(autodraw.Auth.IsLoggedIn
            ? "\nLogged in as " + autodraw.Auth.CurrentUser + " (" + autodraw.Auth.Role + (autodraw.Auth.IsVerified ? ", verified" : ", unverified") + ")"
            : "\nNot logged in - run ADLOGIN.");
        if (!autodraw.Auth.IsLoggedIn) return;

        if (!await EnsureProject(ed)) return;
        var data = autodraw.AutoDraw.CurrentProjectData!;

        ed.WriteMessage("\nProject " + data.ProjectId + " - " + data.ProjectName);

        var meta = data.AutodrawMeta;
        var steps = data.AutodrawConfig?.Steps;
        if (steps != null && meta != null && meta.CurrentStep < steps.Count)
        {
            var step = steps[meta.CurrentStep];
            string substepLabel = meta.CurrentSubstep < step.Substeps.Count
                ? step.Substeps[meta.CurrentSubstep].Label
                : "(past the end)";
            ed.WriteMessage("\nNext: " + step.Label + " / " + substepLabel);
        }
        if (meta != null && meta.IsComplete) ed.WriteMessage("\nThis job is complete.");

        // Whether the drawing is in step with the record - the thing that goes
        // wrong after a restart, and is otherwise invisible.
        int stamped = DxfTransferService.CountStamped(db);
        int onRecord = data.CurrentArtifact?.EntityCount ?? 0;
        ed.WriteMessage("\nDrawing holds " + stamped + " server entities; the record has " + onRecord + ".");
        if (stamped == 0 && onRecord > 0)
        {
            ed.WriteMessage("\n  -> out of step. Run ADSTART to lay the job back in.");
        }
        else if (stamped > 0)
        {
            ed.WriteMessage("\n  -> in step. ADCONTINUE is safe.");
        }

        if (data.Submission?.Layers != null)
        {
            ed.WriteMessage("\nSubmitting layers: " + string.Join(", ", data.Submission.Layers));
        }
    }

    [CommandMethod("ADBACK")]
    public async void BackAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return;
        }
        if (!await EnsureProject(ed)) return;

        int projectId = autodraw.AutoDraw.CurrentProjectId!.Value;

        try
        {
            if (!AskWhichSubstep(ed, "Back", " and everything after it",
                                 out string toSubstep))
            { ed.WriteMessage("\nCancelled."); return; }

            var result = await autodraw.AutoDraw.Back(projectId, toSubstep);
            if (!result.Success)
            {
                ed.WriteMessage($"\nCannot go back: {result.Message}");
                return;
            }

            // Stay on the branch being followed. Going back past its fork lands
            // on states named after the line it forked from, and taking that
            // name would send ADFORWARD down the other line.
            if (autodraw.AutoDraw.CurrentBranch == null) autodraw.AutoDraw.NoteBranch(result.Data?.Branch);

            // A full resync: the record is authoritative, so the drawing is
            // wiped and rebuilt from it - MPanel's geometry included, which is
            // why the server sends the full scope back.
            int erased = 0, imported = 0;
            Dictionary<string, string> before;
            using (DocumentLock docLock = doc.LockDocument())
            {
                before = ChangeHighlightService.Snapshot(db);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    erased = DxfTransferService.EraseAllExcept(
                        tr, db, AutoDrawVisualizer.DecorationLayers);
                    tr.Commit();
                }

                imported = DxfTransferService.ImportBase64(db, result.Dxf);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    // Cloning the server's entities in gave them new handles,
                    // so the groups it sent refer to nothing. Rebuild them.
                    RebuildDerived(tr, db, result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord);

                    AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                    AutoDrawVisualizer.ClearInfoLayer(tr, db);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                    AutoDrawVisualizer.DrawStatusBoard(
                        tr, btr,
                        autodraw.AutoDraw.CurrentProjectData!.AutodrawConfig,
                        result.Data?.AutodrawMeta ?? autodraw.AutoDraw.CurrentProjectData!.AutodrawMeta,
                        origin, autodraw.AutoDraw.CurrentBranch);
                    RedrawNotes(tr, db, btr, result.Notes);
                    GridService.DrawLabels(tr, db, btr, result.Grid);

                    tr.Commit();
                }
            }

            await GridService.SyncSnapshots(doc, autodraw.AutoDraw.CurrentProjectId!.Value, result.Grid,
                result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord,
                autodraw.AutoDraw.CurrentProjectData?.AutodrawConfig);
            ed.Regen();
            ChangeHighlightService.Show(doc, before);
            ed.WriteMessage($"\nWiped {erased}, redrew {imported}.");

            var meta = result.Data?.AutodrawMeta;
            if (meta != null)
            {
                ed.WriteMessage($"\nBack at step {meta.CurrentStep}, substep {meta.CurrentSubstep}.");
            }
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    [CommandMethod("ADNOTE")]
    public async void NoteAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return;
        }
        if (!await EnsureProject(ed)) return;

        int projectId = autodraw.AutoDraw.CurrentProjectId!.Value;

        PromptStringOptions options = new PromptStringOptions(
            "\nNote for this state (Enter to clear): ")
        {
            AllowSpaces = true,
        };
        PromptResult answer = ed.GetString(options);
        if (answer.Status != PromptStatus.OK) return;

        try
        {
            var result = await autodraw.AutoDraw.Note(projectId, answer.StringResult?.Trim());
            if (!result.Success)
            {
                ed.WriteMessage("\n" + result.Message);
                return;
            }

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                RedrawNotes(tr, db, btr, result.Notes);
                tr.Commit();
            }

            ed.Regen();
            string where = result.Data?.Target ?? "this state";
            ed.WriteMessage(result.Data?.Pending == true
                ? "\nNoted against " + where + ", held until that step runs."
                : "\nNoted against " + where + ".");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    [CommandMethod("ADBRANCH")]
    public async void BranchAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return;
        }
        if (!await EnsureProject(ed)) return;

        int projectId = autodraw.AutoDraw.CurrentProjectId!.Value;

        try
        {
            var listing = await autodraw.AutoDraw.Branches(projectId);
            if (listing == null || listing.Branches == null || listing.Branches.Count == 0)
            {
                ed.WriteMessage("\nNo branches recorded yet.");
                return;
            }

            ed.WriteMessage("\nBranches:");
            for (int i = 0; i < listing.Branches.Count; i++)
            {
                BranchDTO branch = listing.Branches[i];
                bool atTip = listing.CurrentArtifactId == branch.TipArtifactId;
                ed.WriteMessage("\n  " + (i + 1) + ") " + (branch.Current ? "* " : "  ") + branch.Name
                                + "  at " + branch.StepKey + "." + branch.SubstepKey
                                + ", " + branch.EntityCount + " entities"
                                + (atTip ? "  (here)" : ""));
            }

            // Offered even with a single branch: jumping to its tip is how you
            // undo a run of ADBACKs in one move.
            PromptStringOptions options = new PromptStringOptions("\nLoad which, by number or name <stay>: ")
            {
                AllowSpaces = false,
            };
            PromptResult answer = ed.GetString(options);
            if (answer.Status != PromptStatus.OK) return;

            string wanted = answer.StringResult?.Trim();
            if (string.IsNullOrWhiteSpace(wanted)) return;

            // A number picks off the list; anything else is taken as a name.
            if (int.TryParse(wanted, out int picked))
            {
                if (picked < 1 || picked > listing.Branches.Count)
                {
                    ed.WriteMessage("\nNo branch " + picked + ".");
                    return;
                }
                wanted = listing.Branches[picked - 1].Name;
            }

            BranchDTO chosen = listing.Branches.FirstOrDefault(b => b.Name == wanted);
            if (chosen != null && listing.CurrentArtifactId == chosen.TipArtifactId)
            {
                ed.WriteMessage("\nAlready at the tip of " + wanted + ".");
                return;
            }

            var result = await autodraw.AutoDraw.SwitchBranch(projectId, wanted);
            if (!result.Success)
            {
                ed.WriteMessage("\n" + result.Message);
                return;
            }
            autodraw.AutoDraw.NoteBranch(result.Data?.Branch ?? wanted);

            // Another line may have reached a different point entirely, so the
            // drawing is replaced rather than merged into - the same full
            // resync reverting does.
            int erased = 0, imported = 0;
            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    erased = DxfTransferService.EraseAllExcept(
                        tr, db, AutoDrawVisualizer.DecorationLayers);
                    tr.Commit();
                }

                imported = DxfTransferService.ImportBase64(db, result.Dxf);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    // Cloning the server's entities in gave them new handles,
                    // so the groups it sent refer to nothing. Rebuild them.
                    RebuildDerived(tr, db, result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord);

                    AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                    AutoDrawVisualizer.ClearInfoLayer(tr, db);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                    AutoDrawVisualizer.DrawStatusBoard(
                        tr, btr,
                        autodraw.AutoDraw.CurrentProjectData!.AutodrawConfig,
                        result.Data?.AutodrawMeta ?? autodraw.AutoDraw.CurrentProjectData!.AutodrawMeta,
                        origin, autodraw.AutoDraw.CurrentBranch);
                    RedrawNotes(tr, db, btr, result.Notes);
                    GridService.DrawLabels(tr, db, btr, result.Grid);

                    tr.Commit();
                }
            }

            await GridService.SyncSnapshots(doc, autodraw.AutoDraw.CurrentProjectId!.Value, result.Grid,
                result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord,
                autodraw.AutoDraw.CurrentProjectData?.AutodrawConfig);
            ed.Regen();
            ed.WriteMessage("\nOn " + wanted + ": wiped " + erased + ", redrew " + imported + ".");
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
        }
    }

    [CommandMethod("ADFORWARD")]
    public async void ForwardAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;

        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return;
        }
        if (!await EnsureProject(ed)) return;
        if (StopIfPlaying(ed)) return;

        int projectId = autodraw.AutoDraw.CurrentProjectId!.Value;
        bool auto = PluginSettings.Current.Auto;

        if (!AskWhichSubstep(ed, "Forward", "", out string toSubstep, auto ? "the end" : "one step"))
        { ed.WriteMessage("\nCancelled."); return; }

        if (!auto)
        {
            await ForwardOnce(doc, projectId, toSubstep);
            return;
        }

        // Played one substep at a time rather than jumped in one move, so each
        // state is drawn on the way. A target that is not ahead is refused
        // before the first hop, as the server's own jump refuses it, rather
        // than found out at the end of the job.
        if (toSubstep != null && !IsAhead(toSubstep))
        {
            ed.WriteMessage($"\n{toSubstep} is not ahead of where the job is now.");
            return;
        }

        await Play(doc, async first =>
        {
            ContinueResponseDTO result = await ForwardOnce(doc, projectId, null);
            if (result == null || !result.Success) return false;
            if (toSubstep != null
                && string.Equals(result.Data?.Redone, toSubstep, StringComparison.OrdinalIgnoreCase))
                return false;
            return !(result.Data?.AutodrawMeta?.IsComplete ?? false);
        });
    }

    /// <summary>
    /// One forward, redrawn. Returns the server's reply, or null where the call
    /// itself failed; either way the outcome has already been reported.
    /// </summary>
    private static async Task<ContinueResponseDTO> ForwardOnce(Document doc, int projectId, string toSubstep)
    {
        Editor ed = doc.Editor;
        Database db = doc.Database;

        try
        {
            var result = await autodraw.AutoDraw.Forward(projectId, toSubstep);
            if (!result.Success)
            {
                // Nothing ahead is the ordinary case at the front of the job,
                // not a failure worth dressing up as one.
                ed.WriteMessage($"\n{result.Message}");
                return result;
            }

            // Likewise, the states on the way back up to the branch still carry
            // the older line's name.
            if (autodraw.AutoDraw.CurrentBranch == null) autodraw.AutoDraw.NoteBranch(result.Data?.Branch);

            // The step is not run again - the server restores a drawing it made
            // before - so this is the same full resync reverting does.
            int erased = 0, imported = 0;
            Dictionary<string, string> before;
            using (DocumentLock docLock = doc.LockDocument())
            {
                before = ChangeHighlightService.Snapshot(db);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    erased = DxfTransferService.EraseAllExcept(
                        tr, db, AutoDrawVisualizer.DecorationLayers);
                    tr.Commit();
                }

                imported = DxfTransferService.ImportBase64(db, result.Dxf);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    // Cloning the server's entities in gave them new handles,
                    // so the groups it sent refer to nothing. Rebuild them.
                    RebuildDerived(tr, db, result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord);

                    AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                    AutoDrawVisualizer.ClearInfoLayer(tr, db);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                    AutoDrawVisualizer.DrawStatusBoard(
                        tr, btr,
                        autodraw.AutoDraw.CurrentProjectData!.AutodrawConfig,
                        result.Data?.AutodrawMeta ?? autodraw.AutoDraw.CurrentProjectData!.AutodrawMeta,
                        origin, autodraw.AutoDraw.CurrentBranch);
                    RedrawNotes(tr, db, btr, result.Notes);
                    GridService.DrawLabels(tr, db, btr, result.Grid);

                    tr.Commit();
                }
            }

            await GridService.SyncSnapshots(doc, autodraw.AutoDraw.CurrentProjectId!.Value, result.Grid,
                result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord,
                autodraw.AutoDraw.CurrentProjectData?.AutodrawConfig);
            ed.Regen();
            ChangeHighlightService.Show(doc, before);
            ed.WriteMessage($"\nWiped {erased}, redrew {imported}.");

            var meta = result.Data?.AutodrawMeta;
            if (meta != null)
            {
                ed.WriteMessage(meta.IsComplete
                    ? "\nComplete."
                    : $"\nForward at step {meta.CurrentStep}, substep {meta.CurrentSubstep}.");
            }
            return result;
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Take the next step. ADCONTINUE takes one; ADRUN takes as many as the
    /// server can without a person.
    /// </summary>
    [CommandMethod("ADCONTINUE")]
    public async void ContinueAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;

        if (StopIfPlaying(ed)) return;
        if (!PluginSettings.Current.Auto)
        {
            await Advance(null);
            return;
        }

        // One substep per tick, each submitted and drawn, where ADRUN takes them
        // all on the server and draws once. The first substep goes whatever it
        // is, exactly as a single ADCONTINUE would - a drawn step completes with
        // the drawing the designer has just made. After that it stops short of
        // any substep a person draws, the same place ADRUN stops, rather than
        // submitting the untouched drawing as their work.
        await Play(doc, async first =>
        {
            if (!first && NextIsDrawnByAPerson())
            {
                ed.WriteMessage("\nThe next substep is drawn by hand - stopping here.");
                return false;
            }
            return await Advance(null);
        });
    }

    /// <summary>
    /// Whether the substep the job is at would be drawn by a person rather than
    /// run. Resolved as the server resolves it for a run: the default option,
    /// else the first.
    /// </summary>
    private static bool NextIsDrawnByAPerson()
    {
        var data = autodraw.AutoDraw.CurrentProjectData;
        var meta = autodraw.AutoDraw.CurrentMeta;
        var steps = data?.AutodrawConfig?.Steps;
        if (steps == null || meta == null || meta.CurrentStep >= steps.Count) return false;

        var substeps = steps[meta.CurrentStep].Substeps;
        if (substeps == null || meta.CurrentSubstep >= substeps.Count) return false;

        var options = substeps[meta.CurrentSubstep].Options;
        var option = options?.FirstOrDefault(o => o.IsDefault) ?? options?.FirstOrDefault();
        return option == null || !option.Automated;
    }

    /// <summary>Whether a step.substep key lies at or after where the job is now.</summary>
    private static bool IsAhead(string key)
    {
        var data = autodraw.AutoDraw.CurrentProjectData;
        var meta = autodraw.AutoDraw.CurrentMeta;
        var steps = data?.AutodrawConfig?.Steps;
        if (steps == null || meta == null || meta.IsComplete) return false;

        for (int i = 0; i < steps.Count; i++)
        {
            for (int j = 0; j < steps[i].Substeps.Count; j++)
            {
                if (!string.Equals(steps[i].Key + "." + steps[i].Substeps[j].Key, key,
                                   StringComparison.OrdinalIgnoreCase)) continue;
                return i > meta.CurrentStep || (i == meta.CurrentStep && j >= meta.CurrentSubstep);
            }
        }
        return false;
    }

    // Playing: taking substeps one after another, a tick apart.
    //
    // The command itself ends at its first await, so AutoCAD is free while a job
    // plays and Escape has nothing to cancel. Starting any command stops it
    // instead - ADSTOP says so plainly, but LINE or ZOOM does the same - at the
    // end of the substep in hand, never half way through a redraw.
    private static bool _playing;
    private static bool _stopAsked;

    private static async Task Play(Document doc, Func<bool, Task<bool>> takeOne)
    {
        Editor ed = doc.Editor;
        _playing = true;
        _stopAsked = false;
        doc.CommandWillStart += AskToStop;
        ed.WriteMessage($"\nPlaying, {PluginSettings.Current.TickSeconds:0.##}s a step. Any command (ADSTOP) stops it.");

        try
        {
            for (bool first = true; ; first = false)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                if (!await takeOne(first)) break;
                if (_stopAsked) break;

                // Stacked, every step lands in a column of its own, so the view
                // goes with it rather than leaving the work off the screen.
                if (GridService.Last?.Mode == "stack") GridService.Follow(ed, GridService.Last);

                int wait = (int)(PluginSettings.Current.TickSeconds * 1000 - clock.ElapsedMilliseconds);
                if (wait > 0) await Task.Delay(wait);
                if (_stopAsked) break;
            }
            if (_stopAsked) ed.WriteMessage("\nStopped.");
        }
        finally
        {
            doc.CommandWillStart -= AskToStop;
            _playing = false;
        }
    }

    private static void AskToStop(object sender, CommandEventArgs e) => _stopAsked = true;

    /// <summary>
    /// A command that would move the job while one is already playing only
    /// stops it: two lines of work advancing the same record at once would
    /// interleave their submissions.
    /// </summary>
    private static bool StopIfPlaying(Editor ed)
    {
        if (!_playing) return false;
        ed.WriteMessage("\nStopping the job that is playing - run it again once it has stopped.");
        return true;
    }

    [CommandMethod("ADSTOP")]
    public void StopAutoDraw()
    {
        // Starting this command is what stops a playing job; see Play.
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
            _playing ? "\nStopping after this substep." : "\nNothing is playing.");
    }

    [CommandMethod("ADSETTINGS")]
    public async void SettingsAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        PluginSettings settings = PluginSettings.Current;
        string laidOutAs = settings.Layout + "/" + settings.Direction;

        while (true)
        {
            PromptKeywordOptions options = new PromptKeywordOptions(
                $"\nAuto {(settings.Auto ? "On" : "Off")}, tick {settings.TickSeconds:0.##}s, "
                + $"{settings.Layout} {settings.Direction}. Change [Auto/Tick/Layout/Direction] <done>: ");
            options.Keywords.Add("Auto");
            options.Keywords.Add("Tick");
            options.Keywords.Add("Layout");
            options.Keywords.Add("Direction");
            options.AllowNone = true;

            PromptResult picked = ed.GetKeywords(options);
            if (picked.Status != PromptStatus.OK || string.IsNullOrEmpty(picked.StringResult)) break;

            if (picked.StringResult == "Layout" || picked.StringResult == "Direction")
            {
                bool layout = picked.StringResult == "Layout";
                PromptKeywordOptions which = new PromptKeywordOptions(layout
                    ? "\nEach state [Replace/Stack], drawn over the last or kept beside it: "
                    : "\nStacked states run [Right/Down]: ");
                which.Keywords.Add(layout ? "Replace" : "Right");
                which.Keywords.Add(layout ? "Stack" : "Down");
                which.Keywords.Default = layout ? settings.Layout : settings.Direction;
                PromptResult said = ed.GetKeywords(which);
                if (said.Status != PromptStatus.OK) continue;
                if (layout) settings.Layout = said.StringResult; else settings.Direction = said.StringResult;
                settings.Save();
                continue;
            }

            if (picked.StringResult == "Auto")
            {
                PromptKeywordOptions onOff = new PromptKeywordOptions(
                    "\nADCONTINUE and ADFORWARD keep going on their own [On/Off]: ");
                onOff.Keywords.Add("On");
                onOff.Keywords.Add("Off");
                onOff.Keywords.Default = settings.Auto ? "On" : "Off";
                PromptResult said = ed.GetKeywords(onOff);
                if (said.Status != PromptStatus.OK) continue;
                settings.Auto = said.StringResult == "On";
            }
            else
            {
                PromptDoubleOptions tick = new PromptDoubleOptions("\nSeconds between substeps")
                {
                    AllowNegative = false,
                    AllowZero = true,
                    DefaultValue = settings.TickSeconds,
                    UseDefaultValue = true,
                };
                PromptDoubleResult said = ed.GetDouble(tick);
                if (said.Status != PromptStatus.OK) continue;
                settings.TickSeconds = said.Value;
            }

            settings.Save();
        }

        // The layout is the server's to apply, so a change is seen by asking for
        // the job again in it - the whole drawing, laid out afresh.
        if (settings.Layout + "/" + settings.Direction != laidOutAs && autodraw.AutoDraw.HasActiveProject)
        {
            if (StopIfPlaying(ed)) return;
            await LayIn(doc, autodraw.AutoDraw.CurrentProjectId!.Value);
        }
    }

    /// <summary>
    /// Run on until something needs a person: a step they draw, a question, or
    /// the end of the job.
    ///
    /// The saving is not the round trips but what they carry. Every continue
    /// sends the modelspace up and the drawing back down, so a dozen steps is
    /// two dozen transfers of the whole drawing and as many codec passes. A run
    /// submits once and replies once; the states in between are recorded on the
    /// server and never drawn here, which is why ADBACK and ADFORWARD can still
    /// reach every one of them afterwards.
    /// </summary>
    [CommandMethod("ADRUN")]
    public async void RunAutoDraw()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;

        if (!AskWhichSubstep(ed, "Run", "", out string until))
        { ed.WriteMessage("\nCancelled."); return; }

        // Enter means "as far as you can", which is the reason to use ADRUN at
        // all; a named substep stops there.
        await Advance(string.IsNullOrWhiteSpace(until) ? "all" : until);
    }

    /// <summary>
    /// Submit, take the step or steps, redraw. True when it went and the job
    /// can go on; false when it stopped, failed, or finished.
    /// </summary>
    private async Task<bool> Advance(string run)
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Editor ed = doc.Editor;
        Database db = doc.Database;

        if (!autodraw.Auth.IsLoggedIn)
        {
            ed.WriteMessage("\nPlease login first (ADLOGIN).");
            return false;
        }
        if (!await EnsureProject(ed)) return false;

        int projectId = autodraw.AutoDraw.CurrentProjectId!.Value;
        string path = Path.Combine(Path.GetTempPath(), $"autodraw_submit_{Guid.NewGuid():N}.dxf");

        try
        {
            // 1. Submit the drawing. Everything except the INFO decoration goes;
            //    the server decides what it will adopt.
            int exported;
            using (DocumentLock docLock = doc.LockDocument())
            {
                exported = DxfTransferService.ExportModelspace(
                    db, path, AutoDrawVisualizer.DecorationLayers);
            }
            // A drawing that carries none of the server's entities while the
            // record holds geometry is not in step - submitting it would report
            // every one of them as deleted. That is what happens after a
            // restart if the drawing was never laid back in.
            int stamped = DxfTransferService.CountStamped(db);
            int onRecord = autodraw.AutoDraw.CurrentProjectData?.CurrentArtifact?.EntityCount ?? 0;
            if (stamped == 0 && onRecord > 0)
            {
                ed.WriteMessage($"\nThis drawing holds none of the server's {onRecord} entities.");
                ed.WriteMessage("\nRun ADSTART to lay the job back in before continuing.");
                return false;
            }

            ed.WriteMessage($"\nSubmitting {exported} entities...");

            string chosen = ChooseOption(ed);
            var result = await autodraw.AutoDraw.Continue(projectId, path, chosen, null, null, run);

            // A fork has to be named before the drawing is stored, because the
            // submission is the new line's first artifact. So unlike answering
            // a question, this retry carries the file again.
            string branchName = null;
            if (!result.Success && result.Error == "needs_branch")
            {
                branchName = AskForBranch(ed, result);
                if (branchName == null) { ed.WriteMessage("\nCancelled."); return false; }
                result = await autodraw.AutoDraw.Continue(projectId, path, chosen, null, branchName, run);
                autodraw.AutoDraw.NoteBranch(branchName);
            }

            // A step may ask for values before it can run. Answer and call again
            // without the drawing - it is already submitted.
            if (!result.Success && result.Error == "needs_input" && result.Inputs != null)
            {
                var answers = AskFor(ed, result.Inputs);
                if (answers == null) { ed.WriteMessage("\nCancelled."); return false; }
                // The name goes with it. The submission already put this line on
                // the new branch, so it would be inherited anyway - but relying
                // on that silently drops the branch for any caller that answers
                // without having sent a drawing first.
                result = await autodraw.AutoDraw.Continue(projectId, null, chosen, answers, branchName, run);
            }

            // 2. A gate is a normal outcome, not a failure. Report and stop -
            //    the drawing is left exactly as it was.
            if (!result.Success)
            {
                ed.WriteMessage($"\nStopped: {result.Message}");
                return false;
            }

            if (result.Ran != null && result.Ran.Count > 1)
            {
                ed.WriteMessage("\nRan " + result.Ran.Count + " steps: "
                                + string.Join(", ", result.Ran));
                string stopped = result.Data?.StoppedBecause;
                if (!string.IsNullOrWhiteSpace(stopped))
                {
                    ed.WriteMessage("\nStopped there: " + stopped);
                }
            }

            if (result.SubmittedArtifact != null)
            {
                var art = result.SubmittedArtifact;
                ed.WriteMessage($"\nAccepted {art.EntityCount} entities (ignored {art.IgnoredCount} outside the contract).");
                autodraw.AutoDraw.NoteBranch(art.Branch);
            }

            var carry = result.Data?.Carry;
            if (carry != null && carry.Dropped > 0)
            {
                string layers = carry.ByLayer == null
                    ? ""
                    : " (" + string.Join(", ", carry.ByLayer.Select(p => p.Key + " " + p.Value)) + ")";
                ed.WriteMessage(carry.Applied
                    ? "\nThis step dropped " + carry.Dropped + " entities" + layers
                      + ". ADBACK restores them."
                    : "\nKept everything; this step would have dropped " + carry.Dropped
                      + " entities" + layers + ".");
            }

            // 3. Redraw. Normally the server owns what it drew, so erasing its
            // entities and laying the replacements in is enough. When a step has
            // dropped geometry the whole drawing is rebuilt instead: EraseOwned
            // deliberately spares anything marked owner=external, so MPanel's mesh
            // would otherwise linger in the drawing after leaving the record and
            // be submitted back as new geometry next time.
            int erased = 0, imported = 0;
            Dictionary<string, string> before;
            using (DocumentLock docLock = doc.LockDocument())
            {
                before = ChangeHighlightService.Snapshot(db);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    erased = result.Resync
                        ? DxfTransferService.EraseAllExcept(
                            tr, db, new[] { AutoDrawVisualizer.InfoLayer })
                        : DxfTransferService.EraseOwned(tr, db);
                    tr.Commit();
                }

                imported = DxfTransferService.ImportBase64(db, result.Dxf);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    // Cloning the server's entities in gave them new handles,
                    // so the groups it sent refer to nothing. Rebuild them.
                    RebuildDerived(tr, db, result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord);

                    AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                    AutoDrawVisualizer.ClearInfoLayer(tr, db);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                    AutoDrawVisualizer.DrawStatusBoard(
                        tr, btr,
                        autodraw.AutoDraw.CurrentProjectData!.AutodrawConfig,
                        result.Data?.AutodrawMeta ?? autodraw.AutoDraw.CurrentProjectData!.AutodrawMeta,
                        origin, autodraw.AutoDraw.CurrentBranch);
                    RedrawNotes(tr, db, btr, result.Notes);
                    GridService.DrawLabels(tr, db, btr, result.Grid);

                    tr.Commit();
                }
            }

            await GridService.SyncSnapshots(doc, autodraw.AutoDraw.CurrentProjectId!.Value, result.Grid,
                result.Data?.AutodrawRecord ?? autodraw.AutoDraw.CurrentProjectData?.AutodrawRecord,
                autodraw.AutoDraw.CurrentProjectData?.AutodrawConfig);
            ed.Regen();
            ChangeHighlightService.Show(doc, before);
            ed.WriteMessage($"\nErased {erased}, drew {imported}.");

            var meta = result.Data?.AutodrawMeta;
            if (meta != null)
            {
                ed.WriteMessage(meta.IsComplete
                    ? "\nComplete."
                    : $"\nNow at step {meta.CurrentStep}, substep {meta.CurrentSubstep}.");
            }
            return !(meta?.IsComplete ?? false);
        }
        catch (Exception ex)
        {
            ed.WriteMessage($"\nError: {ex.Message}");
            return false;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
}
    /// <summary>
    /// The derived things a redraw has to rebuild: the groups that let a
    /// designer take hold of a whole piece, and the halos that show how near
    /// two of them may come.
    ///
    /// Order matters - the halos are added to the groups, so the groups have
    /// to exist first.
    /// </summary>
    private static void RebuildDerived(Transaction tr, Database db, AutoDrawRecordDTO record)
    {
        // Cloning the server's entities in gave them new handles, so the
        // groups it sent refer to nothing.
        PieceGroupService.Rebuild(tr, db);

        // Half the gap the server says two pieces need, drawn around each. A
        // guide only: the server measures the real outlines and refuses a nest
        // that is too tight, and it never sees these.
        ClearanceHaloService.Rebuild(tr, db, AutoDrawService.ClearanceFrom(record));
    }
}
