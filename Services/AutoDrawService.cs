using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Projects;
using autodraw_plugin.Models.AutoDraw;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;
using System.Collections.Generic;

namespace autodraw_plugin.Services
{
    public class AutoDrawService
    {
        public int? CurrentProjectId { get; private set; }
        public ProjectDetailsDTO? CurrentProjectData { get; private set; }

        /// <summary>
        /// The line of work loaded in this drawing. Two branches look identical
        /// on screen, so the board says which one is in front of you.
        /// </summary>
        public string? CurrentBranch { get; private set; }

        /// <summary>Remember a branch the server just reported, if it did.</summary>
        public void NoteBranch(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name)) CurrentBranch = name;
        }

        /// <summary>
        /// Where the job stands, as the server last reported it. The copy in
        /// CurrentProjectData is from ADSTART and goes stale with the first
        /// step taken, so anything that needs to know where the job is now -
        /// playing on, or checking a target is still ahead - reads this.
        /// </summary>
        public AutoDrawMetaDTO? CurrentMeta { get; private set; }

        /// <summary>Keep the position a reply reports, if it reports one.</summary>
        private ContinueResponseDTO Remember(ContinueResponseDTO result)
        {
            // A reply that carries no position still deserialises one, all
            // zeros; only a real one says it was initialised.
            AutoDrawMetaDTO? meta = result?.Data?.AutodrawMeta;
            if (meta != null && meta.Initialised) CurrentMeta = meta;
            return result;
        }

        /// <summary>
        /// Fetch a project's automation state and its drawing.
        ///
        /// Asks for the full scope by default, so resuming a job that is past
        /// the MPanel handover brings the mesh and panels back too - the server
        /// cannot regenerate those, and without them a later submission would
        /// read as though they had been deleted.
        /// </summary>
        public async Task StartProject(int projectId, string scope = "full")
        {
            CurrentProjectId = projectId;

            string endpoint = $"/automation/start/{projectId}?drawing_scope={scope}"
                              + $"&layout={Laid("layout")}&direction={Laid("direction")}";
            HttpResponseMessage response = await ApiService.Get(endpoint);
            string json = await response.Content.ReadAsStringAsync();

            // The new structure is a direct object, no "data" wrapper
            CurrentProjectData = JsonConvert.DeserializeObject<ProjectDetailsDTO>(json);
            CurrentMeta = CurrentProjectData?.AutodrawMeta;
            NoteBranch(CurrentProjectData?.CurrentArtifact?.Branch);
        }

        /// <summary>
        /// Submit the drawing and ask the server to take the next step.
        /// Returns the response whether it advanced or stopped at a gate.
        /// </summary>
        public async Task<ContinueResponseDTO> Continue(
            int projectId, string dxfPath, string? selectedOption = null,
            IDictionary<string, string>? answers = null, string? branch = null,
            string? run = null)
        {
            var fields = LaidOut();
            if (!string.IsNullOrEmpty(selectedOption)) fields["selected_option"] = selectedOption;
            if (!string.IsNullOrEmpty(branch)) fields["branch"] = branch;
            // How far to go: absent is one substep, "all" runs until something
            // stops it, a count caps it, a substep key or "3.1" stops there.
            if (!string.IsNullOrEmpty(run)) fields["run"] = run;
            if (answers != null && answers.Count > 0)
            {
                fields["inputs"] = JsonConvert.SerializeObject(answers);
            }

            // Answering a question is a second call about a drawing already
            // submitted, so it goes without the file - resending it would
            // record a duplicate submission.
            HttpResponseMessage response = dxfPath == null
                ? await ApiService.PostForm($"/automation/continue/{projectId}", fields)
                : await ApiService.PostDxf($"/automation/continue/{projectId}", dxfPath, fields);

            return Remember(await Interpret(response, "ADCONTINUE"));
        }

        /// <summary>
        /// Undo the last completed substep. The server hands back the whole
        /// drawing as it stood before that step, for a full redraw.
        /// </summary>
        public async Task<ContinueResponseDTO> Back(int projectId, string? toSubstep = null)
        {
            var fields = LaidOut();
            if (!string.IsNullOrWhiteSpace(toSubstep)) fields["to_substep"] = toSubstep;

            HttpResponseMessage response = await ApiService.PostForm(
                $"/automation/back/{projectId}", fields);

            return Remember(await Interpret(response, "ADBACK"));
        }

        /// <summary>
        /// Attach a note to the state now loaded. Empty text clears it.
        /// </summary>
        public async Task<ContinueResponseDTO> Note(int projectId, string text)
        {
            HttpResponseMessage response = await ApiService.PostForm(
                $"/automation/note/{projectId}",
                new Dictionary<string, string> { ["note"] = text ?? "" });

            return await Interpret(response, "ADNOTE");
        }

        /// <summary>List the lines of work this project holds.</summary>
        public async Task<BranchListDTO?> Branches(int projectId)
        {
            HttpResponseMessage response = await ApiService.Get($"/automation/branches/{projectId}");
            string body = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonConvert.DeserializeObject<BranchListDTO>(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Load another branch's newest state. The drawing is replaced wholesale,
        /// the same as reverting - a different line may have reached a different
        /// point entirely.
        /// </summary>
        public async Task<ContinueResponseDTO> SwitchBranch(int projectId, string name)
        {
            HttpResponseMessage response = await ApiService.PostForm(
                $"/automation/branch/{projectId}",
                new Dictionary<string, string>(LaidOut()) { ["branch"] = name });

            return Remember(await Interpret(response, "ADBRANCH"));
        }

        /// <summary>
        /// Redo the substep that was undone, without running it again. The
        /// server restores a drawing it already made and hands back the whole
        /// of it, exactly as reverting does.
        /// </summary>
        public async Task<ContinueResponseDTO> Forward(int projectId, string? toSubstep = null)
        {
            var fields = LaidOut();
            if (!string.IsNullOrWhiteSpace(toSubstep)) fields["to_substep"] = toSubstep;
            // The line being followed, so a fork behind the branch is crossed
            // towards it rather than stopping to ask which way.
            if (!string.IsNullOrWhiteSpace(CurrentBranch)) fields["branch"] = CurrentBranch;

            HttpResponseMessage response = await ApiService.PostForm(
                $"/automation/forward/{projectId}", fields);

            return Remember(await Interpret(response, "ADFORWARD"));
        }

        /// <summary>
        /// The layout every drawing is asked for in, from the designer's own
        /// settings. The server does the placing - into a cell per item, and a
        /// cell per substep when stacking - and takes it back out of whatever
        /// is submitted, so it has to be told on every call that carries a
        /// drawing either way.
        /// </summary>
        private static Dictionary<string, string> LaidOut() => new()
        {
            ["layout"] = Laid("layout"),
            ["direction"] = Laid("direction"),
        };

        private static string Laid(string what) => (what == "layout"
            ? PluginSettings.Current.Layout
            : PluginSettings.Current.Direction).ToLowerInvariant();

        /// <summary>
        /// One earlier state of the job, laid out in its own column. Read-only:
        /// the job does not move. A state never changes once made, so what
        /// comes back can be kept for as long as the drawing is open.
        /// </summary>
        public async Task<DrawingAtDTO?> DrawingAt(int projectId, int artifactId)
        {
            HttpResponseMessage response = await ApiService.Get(
                $"/automation/drawing/{projectId}/{artifactId}"
                + $"?layout={Laid("layout")}&direction={Laid("direction")}");
            string body = await response.Content.ReadAsStringAsync();
            try
            {
                return JsonConvert.DeserializeObject<DrawingAtDTO>(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Turn a response into a DTO, and make sure a failure always says
        /// something. A 401 or a server error is not the JSON we expect, so
        /// deserialising it silently yields a blank message and the command
        /// reports nothing useful.
        /// </summary>
        private static async Task<ContinueResponseDTO> Interpret(HttpResponseMessage response, string what)
        {
            string body = await response.Content.ReadAsStringAsync();
            ContinueResponseDTO result = null;
            try
            {
                result = JsonConvert.DeserializeObject<ContinueResponseDTO>(body);
            }
            catch (JsonException error)
            {
                return new ContinueResponseDTO
                {
                    Success = false,
                    Message = $"{what}: HTTP {(int)response.StatusCode} - response was not JSON ({error.Message}). "
                              + Excerpt(body),
                };
            }

            if (result == null)
            {
                return new ContinueResponseDTO
                {
                    Success = false,
                    Message = $"{what}: HTTP {(int)response.StatusCode} - empty response. " + Excerpt(body),
                };
            }

            if (!result.Success && string.IsNullOrWhiteSpace(result.Message))
            {
                result.Message = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). " + Excerpt(body);
            }
            return result;
        }

        private static string Excerpt(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
            body = body.Replace("\r", " ").Replace("\n", " ").Trim();
            return body.Length > 300 ? body.Substring(0, 300) + "..." : body;
        }

        public bool HasActiveProject => CurrentProjectId.HasValue && CurrentProjectData != null;
            /// <summary>
        /// The gap the server says two pieces need between them, or null where
        /// no step has said.
        ///
        /// Searched for by name anywhere in any substep's published facts,
        /// rather than in a place agreed with one product. That is the whole
        /// contract: a product that publishes `clearanceMm` gets a guide drawn
        /// at half of it, and one that does not gets none. Nothing here learns
        /// what a panel is, or which step draws the fabric.
        /// </summary>
        public static double? ClearanceFrom(AutoDrawRecordDTO record)
        {
            if (record?.Steps == null) return null;

            foreach (AutoDrawStepStatusDTO step in record.Steps.Values)
            {
                if (step?.Substeps == null) continue;
                foreach (AutoDrawSubstepStatusDTO substep in step.Substeps.Values)
                {
                    JToken published = substep?.Metadata?.SelectToken("derived");
                    if (published == null) continue;

                    foreach (JToken found in published.SelectTokens("$..clearanceMm"))
                    {
                        if (found == null || found.Type == JTokenType.Null) continue;
                        double gap = found.Value<double>();
                        if (gap > 0.0) return gap;
                    }
                }
            }
            return null;
        }
    }
}
