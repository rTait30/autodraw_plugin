using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Projects;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.DatabaseServices;

namespace autodraw_plugin.Services
{
    public class AutoDrawService
    {
        public int? CurrentProjectId { get; private set; }
        public ProjectDetailsDTO? CurrentProjectData { get; private set; }

        public async Task StartProject(int projectId)
        {
            CurrentProjectId = projectId;

            // Fetch data
            // Assuming endpoint /automation/start/{id} returns ProjectDetailsDTO structure
            string endpoint = $"/automation/start/{projectId}";
            HttpResponseMessage response = await ApiService.Get(endpoint);

            string json = await response.Content.ReadAsStringAsync();

            // Note: If the API returns { "data": ... } wrapper, validation parsing is needed.
            // Assuming direct object for now based on snippet "JsonConvert.DeserializeObject<ProjectDetails>(json)"
            // But previous prompt showed "data" wrapper. I will parse carefully.
            
            JObject root = JObject.Parse(json);
            if (root["data"] != null)
            {
                CurrentProjectData = root["data"]?.ToObject<ProjectDetailsDTO>();
            }
            else
            {
                // Fallback or direct
                CurrentProjectData = JsonConvert.DeserializeObject<ProjectDetailsDTO>(json);
            }
            
        }
        
        public bool HasActiveProject => CurrentProjectId.HasValue && CurrentProjectData != null;
    }
}
