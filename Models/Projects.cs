using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using autodraw_plugin.Models.AutoDraw;
using Newtonsoft.Json;

namespace autodraw_plugin.Models.Projects;

// Root DTO for /automation/start/{projectId}
public class ProjectDetailsDTO
{
    [JsonProperty("project_id")]
    public int ProjectId { get; set; }

    [JsonProperty("project_name")]
    public string ProjectName { get; set; }

    [JsonProperty("project_attributes")]
    public JToken? ProjectAttributes { get; set; }

    [JsonProperty("item_attributes")]
    public List<JToken> ItemAttributes { get; set; } = new();

    [JsonProperty("autodraw_config")]
    public AutoDrawConfigDTO AutodrawConfig { get; set; } = new();

    [JsonProperty("autodraw_meta")]
    public AutoDrawMetaDTO AutodrawMeta { get; set; } = new();

    [JsonProperty("autodraw_record")]
    public AutoDrawRecordDTO AutodrawRecord { get; set; } = new();

    /// <summary>What this drawing should send back on ADCONTINUE.</summary>
    [JsonProperty("submission")]
    public SubmissionContractDTO Submission { get; set; } = new();

    [JsonProperty("current_artifact")]
    public ArtifactSummaryDTO? CurrentArtifact { get; set; }

    /// <summary>Base64 DXF of the drawing as the server currently holds it.</summary>
    [JsonProperty("dxf")]
    public string? Dxf { get; set; }

    /// <summary>The loaded lineage's notes, for the NOTES layer.</summary>
    [JsonProperty("notes")]
    public List<NoteDTO>? Notes { get; set; }

    [JsonProperty("dxf_scope")]
    public string? DxfScope { get; set; }
}

