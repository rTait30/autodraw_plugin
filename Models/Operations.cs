using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace autodraw_plugin.Models.Operations;

/// <summary>One operation as the server's catalogue lists it.</summary>
public class OperationDTO
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("name")]
    public string Name { get; set; } = "";

    /// <summary>"operation", or "recipe" for one made of others.</summary>
    [JsonProperty("kind")]
    public string Kind { get; set; } = "operation";

    [JsonProperty("inputs")]
    public List<OperationInputDTO> Inputs { get; set; } = new();

    /// <summary>What has to be drawn (`roles`) or known (`facts`) before it can run.</summary>
    [JsonProperty("needs")]
    public JObject? Needs { get; set; }

    /// <summary>A recipe's steps as typed - `seam_marks, label_panels(height=150)` - to edit and save again.</summary>
    [JsonProperty("written")]
    public string? Written { get; set; }

    [JsonProperty("label")]
    public string? Label { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    /// <summary>Why a recipe can no longer run - a step since removed - where it cannot.</summary>
    [JsonProperty("broken")]
    public string? Broken { get; set; }

    /// <summary>A recipe's operations in the order they run - what a run's `start` counts.</summary>
    [JsonProperty("operations")]
    public List<string> Operations { get; set; } = new();

    /// <summary>Whether it runs on nothing drawn at all - one that draws a job from its data.</summary>
    public bool NeedsNothingDrawn => Needs?["roles"] is not JArray roles || roles.Count == 0;
}

/// <summary>
/// A recipe as the server saved it, with what it worked out it asks, needs and
/// makes - for the designer to check.
/// </summary>
public class RecipeDTO
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("operations")]
    public List<string> Operations { get; set; } = new();

    [JsonProperty("written")]
    public string? Written { get; set; }

    [JsonProperty("inputs")]
    public List<OperationInputDTO> Inputs { get; set; } = new();

    [JsonProperty("needs")]
    public JObject? Needs { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();
}

public class RecipeSavedDTO
{
    [JsonProperty("recipe")]
    public RecipeDTO? Recipe { get; set; }

    /// <summary>False where the newest version already had these steps.</summary>
    [JsonProperty("created")]
    public bool Created { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }
}

public class OperationCatalogueDTO
{
    [JsonProperty("operations")]
    public List<OperationDTO> Operations { get; set; } = new();

}

/// <summary>
/// A value an operation asks for. Typed, so it can be asked with the prompt
/// that suits it - a whole number, a length that can be picked, a choice.
/// </summary>
public class OperationInputDTO
{
    [JsonProperty("key")]
    public string Key { get; set; } = "";

    [JsonProperty("label")]
    public string? Label { get; set; }

    /// <summary>integer, number, length, text, choice or boolean.</summary>
    [JsonProperty("type")]
    public string? Type { get; set; }

    [JsonProperty("min")]
    public double? Min { get; set; }

    [JsonProperty("max")]
    public double? Max { get; set; }

    [JsonProperty("choices")]
    public List<string>? Choices { get; set; }

    [JsonProperty("default")]
    public JToken? Default { get; set; }

    /// <summary>Which item this answer is for, where one is asked per item.</summary>
    [JsonProperty("item")]
    public int? Item { get; set; }

    /// <summary>The answer last sent, where the server would not take it.</summary>
    [JsonProperty("rejected")]
    public JToken? Rejected { get; set; }
}

/// <summary>What a run of an operation hands back, done or refused.</summary>
public class OperationResultDTO
{
    [JsonProperty("operation")]
    public string? Operation { get; set; }

    /// <summary>A project's drawing this reply leaves it at.</summary>
    [JsonProperty("artifact_id")]
    public int? ArtifactId { get; set; }

    /// <summary>Pictures of a project's earlier states, base64 DXF, when stacking.</summary>
    [JsonProperty("history")]
    public string? History { get; set; }

    /// <summary>The whole drawing after the operation, base64 DXF.</summary>
    [JsonProperty("drawing")]
    public string? Drawing { get; set; }

    [JsonProperty("notes")]
    public string? Notes { get; set; }

    [JsonProperty("warnings")]
    public List<string>? Warnings { get; set; }

    /// <summary>What was read off the layers of what was sent.</summary>
    [JsonProperty("interpreted")]
    public List<string>? Interpreted { get; set; }

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    [JsonProperty("inputs")]
    public List<OperationInputDTO>? Inputs { get; set; }

    /// <summary>Which step of a recipe refused or asked, counted from one, and of how many.</summary>
    [JsonProperty("step")]
    public int? Step { get; set; }

    [JsonProperty("steps")]
    public int? Steps { get; set; }

    /// <summary>The operations that ran, in order.</summary>
    [JsonProperty("ran")]
    public List<string>? Ran { get; set; }

    /// <summary>What the run had settled when it ended, sent back with a run that goes on from it.</summary>
    [JsonProperty("facts")]
    public JToken? Facts { get; set; }

    /// <summary>Where a recipe stopped for a person to review what a step drew; null when it ran through.</summary>
    [JsonProperty("paused")]
    public PausedDTO? Paused { get; set; }

    /// <summary>
    /// With `needs_selection`: the job's items, each to be selected - or left
    /// out - and sent back as that item's own drawing.
    /// </summary>
    [JsonProperty("items")]
    public List<SelectionItemDTO>? Items { get; set; }

    /// <summary>
    /// Asked for with `states=1`: each drawing along a project's history,
    /// oldest first, each only what it drew and where it was made.
    /// </summary>
    [JsonProperty("states")]
    public List<OperationStateDTO>? States { get; set; }

    /// <summary>
    /// On a project's drawing: where a recipe stopped for a person on its run -
    /// a step-through, a review, or a save drawn up to MPanel - while the run is
    /// still at that drawing. What ADGO goes on with after a restart.
    /// </summary>
    [JsonProperty("waiting")]
    public WaitingDTO? Waiting { get; set; }

    /// <summary>On a project's drawing: each state along its history, oldest first, with its operation.</summary>
    [JsonProperty("history_notes")]
    public List<JObject>? HistoryNotes { get; set; }
}

/// <summary>A run stopped for a person, as the server keeps it on the run.</summary>
public class WaitingDTO
{
    /// <summary>What was run - a recipe's name, or names typed in - to run again from `Next`.</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("step")]
    public int Step { get; set; }

    [JsonProperty("steps")]
    public int Steps { get; set; }

    [JsonProperty("next")]
    public int Next { get; set; }

    [JsonProperty("operation")]
    public string? Operation { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }

    /// <summary>What a person answered, sent again going on.</summary>
    [JsonProperty("inputs")]
    public JObject? Inputs { get; set; }
}

/// <summary>
/// One row of a project's operation history. An operation over the job's items
/// is a row per item it changed (`ItemId`), all but its last `Partial`; one
/// over the whole job is a single row with no item.
/// </summary>
public class OperationStateDTO
{
    [JsonProperty("artifact_id")]
    public int ArtifactId { get; set; }

    [JsonProperty("operation")]
    public string? Operation { get; set; }

    [JsonProperty("recipe")]
    public string? Recipe { get; set; }

    /// <summary>"operation", or how else it came to be - a drawing sent back from CAD.</summary>
    [JsonProperty("source")]
    public string? Source { get; set; }

    [JsonProperty("item_id")]
    public int? ItemId { get; set; }

    [JsonProperty("partial")]
    public bool Partial { get; set; }

    /// <summary>What this row drew and nothing else, base64 DXF, where it was made.</summary>
    [JsonProperty("drawing")]
    public string? Drawing { get; set; }
}

/// <summary>A run's log, as `.../log` reads it and a note written hands it back.</summary>
public class RunLogDTO
{
    [JsonProperty("log")]
    public List<RunLogEntryDTO> Log { get; set; } = new();

    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }
}

/// <summary>
/// One operation along a run's history - its rows by item taken together - with
/// what it said and what people wrote while the run was at it. The first entry
/// may be notes written before anything was drawn, with no operation.
/// </summary>
public class RunLogEntryDTO
{
    [JsonProperty("artifact_id")]
    public int? ArtifactId { get; set; }

    [JsonProperty("operation")]
    public string? Operation { get; set; }

    [JsonProperty("recipe")]
    public string? Recipe { get; set; }

    [JsonProperty("source")]
    public string? Source { get; set; }

    [JsonProperty("items")]
    public List<int>? Items { get; set; }

    /// <summary>What the operation said when it ran.</summary>
    [JsonProperty("notes")]
    public string? Notes { get; set; }

    [JsonProperty("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>What people wrote.</summary>
    [JsonProperty("written")]
    public List<RunNoteDTO>? Written { get; set; }
}

public class RunNoteDTO
{
    [JsonProperty("item_id")]
    public int? ItemId { get; set; }

    [JsonProperty("user")]
    public string? User { get; set; }

    [JsonProperty("text")]
    public string? Text { get; set; }

    [JsonProperty("created_at")]
    public string? CreatedAt { get; set; }
}

/// <summary>
/// A recipe stopped after a step marked for review: the step (from one) of how
/// many, what to say, and `next`, the step to go on from once the drawing has
/// been looked at and changed.
/// </summary>
public class PausedDTO
{
    [JsonProperty("step")]
    public int Step { get; set; }

    [JsonProperty("steps")]
    public int Steps { get; set; }

    [JsonProperty("next")]
    public int Next { get; set; }

    [JsonProperty("operation")]
    public string? Operation { get; set; }

    [JsonProperty("message")]
    public string? Message { get; set; }
}

/// <summary>One item a designer is asked to select the geometry of.</summary>
public class SelectionItemDTO
{
    [JsonProperty("item")]
    public int Item { get; set; }

    [JsonProperty("label")]
    public string? Label { get; set; }
}
