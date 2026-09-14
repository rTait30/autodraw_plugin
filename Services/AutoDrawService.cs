using System;
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

            string endpoint = $"/automation/start/{projectId}?drawing_scope={scope}";
            HttpResponseMessage response = await ApiService.Get(endpoint);
            string json = await response.Content.ReadAsStringAsync();

            // The new structure is a direct object, no "data" wrapper
            CurrentProjectData = JsonConvert.DeserializeObject<ProjectDetailsDTO>(json);
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
            var fields = new Dictionary<string, string>();
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

            return await Interpret(response, "ADCONTINUE");
        }

        /// <summary>
        /// Undo the last completed substep. The server hands back the whole
        /// drawing as it stood before that step, for a full redraw.
        /// </summary>
        public async Task<ContinueResponseDTO> Back(int projectId, string? toSubstep = null)
        {
            var fields = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(toSubstep)) fields["to_substep"] = toSubstep;

            HttpResponseMessage response = await ApiService.PostForm(
                $"/automation/back/{projectId}", fields);

            return await Interpret(response, "ADBACK");
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
                new Dictionary<string, string> { ["branch"] = name });

            return await Interpret(response, "ADBRANCH");
        }

        /// <summary>
        /// Redo the substep that was undone, without running it again. The
        /// server restores a drawing it already made and hands back the whole
        /// of it, exactly as reverting does.
        /// </summary>
        public async Task<ContinueResponseDTO> Forward(int projectId, string? toSubstep = null)
        {
            var fields = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(toSubstep)) fields["to_substep"] = toSubstep;

            HttpResponseMessage response = await ApiService.PostForm(
                $"/automation/forward/{projectId}", fields);

            return await Interpret(response, "ADFORWARD");
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
    }

}
