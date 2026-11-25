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
