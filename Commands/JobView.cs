using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Operations;
using autodraw_plugin.Services;

namespace autodraw_plugin.Commands;

/// <summary>
/// Showing the open job: laying its drawing in, and saying where it stands.
/// Every command that changes what is drawn ends here, so they all end the
/// same way - see AGENTS.md, "The redraw contract".
/// </summary>
internal static class JobView
{
    /// <summary>
    /// Replace the drawing with the job's as just sent: everything but the
    /// decoration goes, the drawing comes in, and its history beside it - the
    /// pictures of each state on their own layers, stacked or in a grid.
    /// What the reply says of the job is taken as the job's state, the run
    /// waiting on a person included.
    /// </summary>
    public static void LayIn(Document doc, OperationResultDTO drawn)
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
            // Stacked or in a grid, the server sent the drawing where it was
            // made and every state along its history, laid out beside it here
            // on the layers each was drawn on.
            if (OperationGridService.Shown)
                OperationGridService.Lay(doc, drawn.States, PluginSettings.Current.Direction,
                                         byItem: PluginSettings.Current.OperationsGrid);
        }
        doc.Editor.Regen();

        Job.CurrentArtifact = drawn.ArtifactId;
        Job.At = drawn.HistoryNotes?.LastOrDefault()?.Value<string>("operation") ?? drawn.Operation;
        if (drawn.States != null)
            Job.Items = drawn.States.Where(state => state.ItemId.HasValue)
                .Select(state => state.ItemId!.Value).Distinct().OrderBy(id => id).ToList();

        // The run says where it waits for a person, whoever stopped it - this
        // AutoCAD before a restart, another machine, or a save drawn up to a
        // step done by hand. A run run since says so again (Hold) after this.
        if (drawn.Waiting != null)
            Job.Paused = new PausedRun
            {
                ProjectId = Job.ProjectId, Name = drawn.Waiting.Name,
                Inputs = drawn.Waiting.Inputs ?? new JObject(), Next = drawn.Waiting.Next,
                Step = drawn.Waiting.Step, Steps = drawn.Waiting.Steps, Message = drawn.Waiting.Message,
            };
        else if (Job.Paused?.ProjectId != null)
            Job.Paused = null;
    }

    /// <summary>
    /// A run that stopped part way is held, with what it needs to go on, until
    /// the designer has looked and runs ADGO; one that ran through leaves
    /// nothing waiting.
    /// </summary>
    public static void Hold(OperationResultDTO result, string chosen, JObject inputs, int? projectId)
    {
        if (result.Paused == null)
        {
            Job.Paused = null;
            return;
        }
        Job.Paused = new PausedRun
        {
            ProjectId = projectId, Name = chosen, Inputs = inputs, Next = result.Paused.Next,
            Step = result.Paused.Step, Steps = result.Paused.Steps, Message = result.Paused.Message,
            // A free run's server keeps nothing, so it is handed what it settled.
            Facts = projectId.HasValue ? null : result.Facts,
        };
    }

    /// <summary>What is waiting, if anything, and the one line of where things stand.</summary>
    public static void Report(Editor ed)
    {
        PausedRun? paused = Job.Paused;
        if (paused != null)
            ed.WriteMessage($"\n{paused.Name} stopped after step {paused.Step} of {paused.Steps}: "
                            + (paused.Message ?? "review the drawing, change it if need be."));
        ed.WriteMessage("\n" + Job.Status());
    }
}
