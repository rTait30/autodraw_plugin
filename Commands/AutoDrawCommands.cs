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
                if (ent != null && ent.Layer != AutoDrawVisualizer.InfoLayer) count++;
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

             int erased = 0, imported = 0;

             using (DocumentLock docLock = doc.LockDocument())
             {
                 // A full resync: everything except the INFO board goes, and the
                 // server's full-scope copy replaces it - MPanel's geometry
                 // included, so a half-done job comes back complete.
                 using (Transaction tr = db.TransactionManager.StartTransaction())
                 {
                     erased = DxfTransferService.EraseAllExcept(
                         tr, db, new[] { AutoDrawVisualizer.InfoLayer });
                     tr.Commit();
                 }

                 imported = DxfTransferService.ImportBase64(db, data.Dxf);

                 // Then the board, clear of whatever was just drawn.
                 using (Transaction tr = db.TransactionManager.StartTransaction())
                 {
                     AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                     AutoDrawVisualizer.ClearInfoLayer(tr, db);

                     BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                     BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                     Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                     AutoDrawVisualizer.DrawStatusBoard(tr, btr, data.AutodrawConfig, data.AutodrawMeta, origin);
                     AutoDrawVisualizer.DrawCableSummary(tr, btr, db, data.AutodrawRecord);

                     tr.Commit();
                 }
             }

             ed.Regen();
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
            var result = await autodraw.AutoDraw.Back(projectId);
            if (!result.Success)
            {
                ed.WriteMessage($"\nCannot go back: {result.Message}");
                return;
            }

            // A full resync: the record is authoritative, so the drawing is
            // wiped and rebuilt from it - MPanel's geometry included, which is
            // why the server sends the full scope back.
            int erased = 0, imported = 0;
            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    erased = DxfTransferService.EraseAllExcept(
                        tr, db, new[] { AutoDrawVisualizer.InfoLayer });
                    tr.Commit();
                }

                imported = DxfTransferService.ImportBase64(db, result.Dxf);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                    AutoDrawVisualizer.ClearInfoLayer(tr, db);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                    AutoDrawVisualizer.DrawStatusBoard(
                        tr, btr,
                        autodraw.AutoDraw.CurrentProjectData!.AutodrawConfig,
                        result.Data?.AutodrawMeta ?? autodraw.AutoDraw.CurrentProjectData!.AutodrawMeta,
                        origin);
                    AutoDrawVisualizer.DrawCableSummary(tr, btr, db, result.Data?.AutodrawRecord);

                    tr.Commit();
                }
            }

            ed.Regen();
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

    [CommandMethod("ADCONTINUE")]
    public async void ContinueAutoDraw()
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
        string path = Path.Combine(Path.GetTempPath(), $"autodraw_submit_{Guid.NewGuid():N}.dxf");

        try
        {
            // 1. Submit the drawing. Everything except the INFO decoration goes;
            //    the server decides what it will adopt.
            int exported;
            using (DocumentLock docLock = doc.LockDocument())
            {
                exported = DxfTransferService.ExportModelspace(
                    db, path, new[] { AutoDrawVisualizer.InfoLayer });
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
                return;
            }

            ed.WriteMessage($"\nSubmitting {exported} entities...");

            string chosen = ChooseOption(ed);
            var result = await autodraw.AutoDraw.Continue(projectId, path, chosen);

            // A step may ask for values before it can run. Answer and call again
            // without the drawing - it is already submitted.
            if (!result.Success && result.Error == "needs_input" && result.Inputs != null)
            {
                var answers = AskFor(ed, result.Inputs);
                if (answers == null) { ed.WriteMessage("\nCancelled."); return; }
                result = await autodraw.AutoDraw.Continue(projectId, null, chosen, answers);
            }

            // 2. A gate is a normal outcome, not a failure. Report and stop -
            //    the drawing is left exactly as it was.
            if (!result.Success)
            {
                ed.WriteMessage($"\nStopped: {result.Message}");
                return;
            }

            if (result.SubmittedArtifact != null)
            {
                var art = result.SubmittedArtifact;
                ed.WriteMessage($"\nAccepted {art.EntityCount} entities (ignored {art.IgnoredCount} outside the contract).");
            }

            // 3. Redraw: erase what the server owns, lay its replacement in.
            int erased = 0, imported = 0;
            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    erased = DxfTransferService.EraseOwned(tr, db);
                    tr.Commit();
                }

                imported = DxfTransferService.ImportBase64(db, result.Dxf);

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    AutoDrawVisualizer.EnsureInfoLayer(tr, db);
                    AutoDrawVisualizer.ClearInfoLayer(tr, db);

                    BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    Point3d origin = AutoDrawVisualizer.BoardOrigin(tr, db);
                    AutoDrawVisualizer.DrawStatusBoard(
                        tr, btr,
                        autodraw.AutoDraw.CurrentProjectData!.AutodrawConfig,
                        result.Data?.AutodrawMeta ?? autodraw.AutoDraw.CurrentProjectData!.AutodrawMeta,
                        origin);
                    AutoDrawVisualizer.DrawCableSummary(tr, btr, db, result.Data?.AutodrawRecord);

                    tr.Commit();
                }
            }

            ed.Regen();
            ed.WriteMessage($"\nErased {erased}, drew {imported}.");

            var meta = result.Data?.AutodrawMeta;
            if (meta != null)
            {
                ed.WriteMessage(meta.IsComplete
                    ? "\nComplete."
                    : $"\nNow at step {meta.CurrentStep}, substep {meta.CurrentSubstep}.");
            }
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
}
