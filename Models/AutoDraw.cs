using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace autodraw_plugin.Models.AutoDraw;


public class AutoDrawConfigDTO
{
    [JsonProperty("stepCount")]
    public int StepCount { get; set; }

    [JsonProperty("steps")]
    public List<ConfigStepDTO> Steps { get; set; } = new();
}

public class ConfigStepDTO
{
    [JsonProperty("key")]
    public string Key { get; set; }

    [JsonProperty("label")]
    public string Label { get; set; }

    [JsonProperty("show")]
    public List<ShowRuleDTO> Show { get; set; } = new();

    [JsonProperty("substeps")]
    public List<ConfigSubstepDTO> Substeps { get; set; } = new();
}

public class ShowRuleDTO
{
    [JsonProperty("query")]
    public string Query { get; set; }

    [JsonProperty("value")]
    public string Value { get; set; }
}

public class ConfigSubstepDTO
{
    [JsonProperty("key")]
    public string Key { get; set; }

    [JsonProperty("label")]
    public string Label { get; set; }

    [JsonProperty("method")]
    public string Method { get; set; }

    [JsonProperty("options")]
    public List<ConfigSubstepOptionDTO> Options { get; set; } = new();
}

public class ConfigSubstepOptionDTO
{
    [JsonProperty("automated")]
    public bool Automated { get; set; }

    [JsonProperty("is_default")]
    public bool IsDefault { get; set; }

    [JsonProperty("key")]
    public string Key { get; set; }

    [JsonProperty("label")]
    public string Label { get; set; }

    [JsonProperty("software")]
    public string Software { get; set; }
}

public class AutoDrawMetaDTO
{
    [JsonProperty("current_step")]
    public int CurrentStep { get; set; }

    [JsonProperty("current_substep")]
    public int CurrentSubstep { get; set; }

    [JsonProperty("initialised")]
    public bool Initialised { get; set; }

    [JsonProperty("is_complete")]
    public bool IsComplete { get; set; }

    [JsonProperty("last_updated")]
    public string LastUpdated { get; set; }
}

public class AutoDrawRecordDTO
{
    [JsonProperty("created_at")]
    public string CreatedAt { get; set; }

    /// <summary>
    /// Geometry is not carried in the record - it lives in append-only DXF
    /// artifacts on the server. This points at the current one.
    /// </summary>
    [JsonProperty("current_artifact_id")]
    public int? CurrentArtifactId { get; set; }

    [JsonProperty("steps")]
    public Dictionary<string, AutoDrawStepStatusDTO> Steps { get; set; } = new();
}

public class AutoDrawStepStatusDTO
{
    [JsonProperty("label")]
    public string Label { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("substeps")]
    public Dictionary<string, AutoDrawSubstepStatusDTO> Substeps { get; set; } = new();
}

public class AutoDrawSubstepStatusDTO
{
    [JsonProperty("label")]
    public string Label { get; set; }

    [JsonProperty("metadata")]
    public JToken Metadata { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    /// <summary>The artifact this substep produced, if it has run.</summary>
    [JsonProperty("artifact_id")]
    public int? ArtifactId { get; set; }
}

/// <summary>
/// The whitelist the plugin exports by: an entity goes back if its layer is
/// listed, or it carries one of these XDATA appids. Everything else - the INFO
/// status board, a title block, scratch marks - stays in the drawing.
/// </summary>
public class SubmissionContractDTO
{
    [JsonProperty("layers")]
    public List<string> Layers { get; set; } = new();

    [JsonProperty("appids")]
    public List<string> AppIds { get; set; } = new();
}

/// <summary>Response from /automation/continue. On a gate, Success is false and Message says why.</summary>
public class ContinueResponseDTO
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    /// <summary>"needs_input" means the step asked a question, not that it failed.</summary>
    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("inputs")]
    public List<InputRequestDTO>? Inputs { get; set; }

    /// <summary>With "needs_branch": the lines already growing from this state.</summary>
    [JsonProperty("branches")]
    public List<string>? Branches { get; set; }

    /// <summary>With "needs_branch": a name to offer, free of collisions.</summary>
    [JsonProperty("suggested")]
    public string? Suggested { get; set; }

    [JsonProperty("data")]
    public ContinueDataDTO? Data { get; set; }

    [JsonProperty("submitted_artifact")]
    public ArtifactSummaryDTO? SubmittedArtifact { get; set; }

    /// <summary>Every substep this call took, when it took more than one.</summary>
    [JsonProperty("ran")]
    public List<string>? Ran { get; set; }

    [JsonProperty("drawing")]
    public string? Dxf { get; set; }

    [JsonProperty("drawing_scope")]
    public string? DxfScope { get; set; }

    /// <summary>
    /// The step dropped geometry the plugin would not erase on its own, so the
    /// drawing has to be rebuilt from the record rather than merged into.
    /// </summary>
    [JsonProperty("resync")]
    public bool Resync { get; set; }

    /// <summary>The loaded lineage's notes, to lay out on the NOTES layer.</summary>
    [JsonProperty("notes")]
    public List<NoteDTO>? Notes { get; set; }
}

/// <summary>A value a step asked for before it can run.</summary>
public class InputRequestDTO
{
    [JsonProperty("key")]
    public string Key { get; set; }

    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("default")]
    public string? Default { get; set; }

    [JsonProperty("help")]
    public string? Help { get; set; }
}

public class ContinueDataDTO
{
    [JsonProperty("autodraw_meta")]
    public AutoDrawMetaDTO AutodrawMeta { get; set; } = new();

    /// <summary>Why a run stopped short, when it did. Null on a clean finish.</summary>
    [JsonProperty("stopped_because")]
    public string? StoppedBecause { get; set; }

    [JsonProperty("autodraw_record")]
    public AutoDrawRecordDTO AutodrawRecord { get; set; } = new();

    [JsonProperty("carry")]
    public CarryReportDTO? Carry { get; set; }

    /// <summary>The line of work now loaded, where the server reports one.</summary>
    [JsonProperty("branch")]
    public string? Branch { get; set; }

    /// <summary>Which substep a note was filed against.</summary>
    [JsonProperty("target")]
    public string? Target { get; set; }

    /// <summary>True when that substep has not run yet and the note is waiting.</summary>
    [JsonProperty("pending")]
    public bool Pending { get; set; }
}

/// <summary>A person's note against one state in the lineage.</summary>
public class NoteDTO
{
    [JsonProperty("step_label")]
    public string StepLabel { get; set; }

    [JsonProperty("substep_label")]
    public string SubstepLabel { get; set; }

    [JsonProperty("branch")]
    public string? Branch { get; set; }

    [JsonProperty("artifact_id")]
    public int ArtifactId { get; set; }

    [JsonProperty("note")]
    public string Note { get; set; }

    /// <summary>What a person added afterwards, kept apart from the step's own.</summary>
    [JsonProperty("user_note")]
    public string? UserNote { get; set; }

    /// <summary>Written for a substep that has not run yet, so nothing exists to hold it.</summary>
    [JsonProperty("pending")]
    public bool Pending { get; set; }
}

/// <summary>One line of work, from /automation/branches.</summary>
public class BranchDTO
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("tip_artifact_id")]
    public int TipArtifactId { get; set; }

    [JsonProperty("step_key")]
    public string? StepKey { get; set; }

    [JsonProperty("substep_key")]
    public string? SubstepKey { get; set; }

    [JsonProperty("entity_count")]
    public int EntityCount { get; set; }

    [JsonProperty("current")]
    public bool Current { get; set; }
}

public class BranchListDTO
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("branches")]
    public List<BranchDTO>? Branches { get; set; }

    /// <summary>Where the drawing stands now, to spot a jump that would do nothing.</summary>
    [JsonProperty("current_artifact_id")]
    public int? CurrentArtifactId { get; set; }
}

/// <summary>What a step's carry rule removed, or would have removed.</summary>
public class CarryReportDTO
{
    [JsonProperty("dropped")]
    public int Dropped { get; set; }

    /// <summary>False when the run was told to keep everything (carry=off).</summary>
    [JsonProperty("applied")]
    public bool Applied { get; set; }

    [JsonProperty("by_layer")]
    public Dictionary<string, int>? ByLayer { get; set; }
}

public class ArtifactSummaryDTO
{
    [JsonProperty("branch")]
    public string? Branch { get; set; }

    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("step_key")]
    public string? StepKey { get; set; }

    [JsonProperty("substep_key")]
    public string? SubstepKey { get; set; }

    [JsonProperty("source")]
    public string? Source { get; set; }

    [JsonProperty("entity_count")]
    public int EntityCount { get; set; }

    [JsonProperty("ignored_count")]
    public int IgnoredCount { get; set; }

    [JsonProperty("created_at")]
    public string? CreatedAt { get; set; }
}