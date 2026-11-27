using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using autodraw_plugin.Models.Operations;

namespace autodraw_plugin.Services;

/// <summary>
/// The server's free operations: any drawing sent, the operation done, the
/// whole drawing handed back. No project, no record - so none of the record's
/// checks apply, and what was sent is what gets replaced.
/// </summary>
public static class OperationService
{
    public static async Task<OperationCatalogueDTO> Catalogue()
    {
        HttpResponseMessage response = await ApiService.Get("/operations");
        string body = await response.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<OperationCatalogueDTO>(body) ?? new();
    }

    /// <summary>
    /// Send a DXF to an operation with the answers so far. A refusal - a value
    /// still to ask for, a role the drawing lacks - comes back as a 422 worth
    /// reading, so the status is not thrown on.
    /// </summary>
    /// <summary>
    /// Run an operation free on a drawing. With `itemPaths`, what was selected
    /// for each item goes as that item's own drawing (`drawing_&lt;item&gt;`)
    /// in place of `dxfPath`, which may then be null. `start` and `facts` go on
    /// with a recipe from where it stopped for review (`Job.Paused`).
    /// </summary>
    public static async Task<OperationResultDTO> Run(string id, string? dxfPath, JObject inputs,
                                                     int objectFloor,
                                                     IDictionary<int, string>? itemPaths = null,
                                                     int start = 1, JToken? facts = null)
    {
        var fields = new Dictionary<string, string>
        {
            // A designer is here to look, so a recipe stops after each step
            // it marks for review (`suggest_nest?`) instead of running on -
            // or after every operation, stepping through it (ADOPTIONS).
            ["pause"] = Pause,
            // Pieces come back numbered above every piece already in the
            // drawing, so none is grouped with one it is not part of.
            ["object_floor"] = objectFloor.ToString(),
            ["inputs"] = inputs.ToString(Formatting.None),
            // The designer's layout, as every automation call carries it:
            // replace hands the drawing back where it was, stack beside it.
            ["layout"] = PluginSettings.Current.Layout.ToLowerInvariant(),
            ["direction"] = PluginSettings.Current.Direction.ToLowerInvariant(),
        };
        if (Job.AnswersFrom.HasValue) fields["project_id"] = Job.AnswersFrom.Value.ToString();
        if (start > 1) fields["start"] = start.ToString();
        if (facts != null && facts.Type != JTokenType.Null) fields["facts"] = facts.ToString(Formatting.None);
        // The states along the way, as deep as the designer asked to see them.
        int depth = PluginSettings.Current.StepsDepth;
        if (depth != 0) fields["steps_depth"] = depth < 0 ? "all" : depth.ToString();
        var files = new Dictionary<string, string>();
        if (itemPaths != null)
            foreach (var pair in itemPaths) files[$"drawing_{pair.Key}"] = pair.Value;
        else if (dxfPath != null)
            files["drawing"] = dxfPath;
        HttpResponseMessage response = await ApiService.PostDxfFiles(
            $"/operations/{Uri.EscapeDataString(id)}/run", files, fields);
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            OperationResultDTO? result = JsonConvert.DeserializeObject<OperationResultDTO>(body);
            if (result != null)
            {
                if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(result.Error))
                    result.Error = "http_" + (int)response.StatusCode;
                return result;
            }
        }
        catch (JsonException)
        {
        }
        return new OperationResultDTO
        {
            Error = "http_" + (int)response.StatusCode,
            Message = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) - the response was not JSON.",
        };
    }

    /// <summary>Where a recipe stops for the designer: after every operation, or only at a review.</summary>
    private static string Pause => PluginSettings.Current.StepThrough ? "each" : "reviews";

    /// <summary>
    /// How a job's drawing is asked for. The server places nothing: the
    /// drawing comes back where it was made, and - stacked or in a grid -
    /// every state along its history (`states`), each on the layers it was
    /// drawn on, for OperationGridService to lay out beside it. The server's
    /// own stacked `history` would flatten them onto one layer.
    /// </summary>
    private static Dictionary<string, string> LaidOut()
    {
        var fields = new Dictionary<string, string>
        {
            ["layout"] = "replace",
            ["direction"] = PluginSettings.Current.Direction.ToLowerInvariant(),
        };
        if (OperationGridService.Shown) fields["states"] = "1";
        return fields;
    }

    public static async Task<OperationResultDTO> ProjectDrawing(int projectId)
    {
        string query = string.Join("&", LaidOut().Select(pair => pair.Key + "=" + pair.Value));
        HttpResponseMessage response = await ApiService.Get(
            $"/projects/{projectId}/operations/drawing?{query}");
        return await Read(response);
    }

    /// <summary>
    /// Run an operation on the project, sending the drawing back with it where
    /// one is given. Refusals - a value to ask, a project moved on - come back
    /// readable rather than thrown. `start` goes on with a recipe from where it
    /// stopped (`Job.Paused`); unlike a free run no facts go with it, since the
    /// server kept what ran.
    /// </summary>
    public static async Task<OperationResultDTO> RunOnProject(
        int projectId, string id, string? dxfPath, JObject inputs, int? baseArtifact, int start = 1)
    {
        var fields = LaidOut();
        fields["inputs"] = inputs.ToString(Formatting.None);
        fields["pause"] = Pause;
        if (start > 1) fields["start"] = start.ToString();
        if (baseArtifact.HasValue) fields["base_artifact_id"] = baseArtifact.Value.ToString();
        string endpoint = $"/projects/{projectId}/operations/{Uri.EscapeDataString(id)}/runs";
        HttpResponseMessage response = dxfPath == null
            ? await ApiService.PostForm(endpoint, fields)
            : await ApiService.PostDxf(endpoint, dxfPath, fields);
        return await Read(response);
    }

    /// <summary>Save the last `count` operations run on the project as the recipe `name`.</summary>
    public static async Task<RecipeSavedDTO> SaveRecipe(int projectId, string name, int count)
    {
        // As a form, which is read rather than thrown on when it is refused.
        return await ReadSaved(await ApiService.PostForm(
            $"/projects/{projectId}/recipes",
            new Dictionary<string, string> { ["name"] = name, ["count"] = count.ToString() }));
    }

    /// <summary>
    /// Save `steps`, a line as typed, as the recipe `name` - its next version
    /// where it is one already. The server reads the line; empty fields keep
    /// what the newest version had.
    /// </summary>
    public static async Task<RecipeSavedDTO> SaveRecipe(string name, string steps, string label,
                                                        string description)
    {
        return await ReadSaved(await ApiService.PostForm("/recipes", new Dictionary<string, string>
        {
            ["name"] = name,
            ["steps"] = steps,
            ["label"] = label,
            ["description"] = description,
        }));
    }

    private static async Task<RecipeSavedDTO> ReadSaved(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            RecipeSavedDTO? saved = JsonConvert.DeserializeObject<RecipeSavedDTO>(body);
            if (saved != null) return saved;
        }
        catch (JsonException)
        {
        }
        return new RecipeSavedDTO
        {
            Error = "http_" + (int)response.StatusCode,
            Message = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) - the response was not JSON.",
        };
    }

    /// <summary>Back along the project's history, or forward again.</summary>
    public static async Task<OperationResultDTO> Step(int projectId, bool forward)
    {
        HttpResponseMessage response = await ApiService.PostForm(
            $"/projects/{projectId}/operations/{(forward ? "forward" : "back")}", LaidOut());
        return await Read(response);
    }

    /// <summary>
    /// Write a note on the project's run, against the drawing it is at, for one
    /// item or (null) the whole job. The reply is the run's log.
    /// </summary>
    public static async Task<RunLogDTO> AddNote(int projectId, string text, int? itemId)
    {
        var fields = new Dictionary<string, string> { ["text"] = text };
        if (itemId.HasValue) fields["item_id"] = itemId.Value.ToString();
        return await ReadLog(await ApiService.PostForm($"/projects/{projectId}/operations/notes", fields));
    }

    /// <summary>The project's run log: each operation with what it said and what people wrote.</summary>
    public static async Task<RunLogDTO> Log(int projectId)
    {
        return await ReadLog(await ApiService.Get($"/projects/{projectId}/operations/log"));
    }

    private static async Task<RunLogDTO> ReadLog(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            RunLogDTO? log = JsonConvert.DeserializeObject<RunLogDTO>(body);
            if (log != null)
            {
                if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(log.Error))
                    log.Error = "http_" + (int)response.StatusCode;
                return log;
            }
        }
        catch (JsonException)
        {
        }
        return new RunLogDTO
        {
            Error = "http_" + (int)response.StatusCode,
            Message = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) - the response was not JSON.",
        };
    }

    private static async Task<OperationResultDTO> Read(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        try
        {
            OperationResultDTO? result = JsonConvert.DeserializeObject<OperationResultDTO>(body);
            if (result != null)
            {
                if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(result.Error))
                    result.Error = "http_" + (int)response.StatusCode;
                return result;
            }
        }
        catch (JsonException)
        {
        }
        return new OperationResultDTO
        {
            Error = "http_" + (int)response.StatusCode,
            Message = $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) - the response was not JSON.",
        };
    }

    /// <summary>
    /// Write the given entities out to a DXF. The same as a submission's
    /// export, but of a selection rather than the whole of modelspace.
    /// </summary>
    public static void Export(Database db, ObjectIdCollection ids, string path)
    {
        using (Database dest = new Database(true, true))
        {
            if (ids.Count > 0)
            {
                ObjectId destOwner;
                using (Transaction tr = dest.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(dest.BlockTableId, OpenMode.ForRead);
                    destOwner = bt[BlockTableRecord.ModelSpace];
                    tr.Commit();
                }
                db.WblockCloneObjects(ids, destOwner, new IdMapping(), DuplicateRecordCloning.Replace, false);
            }
            // R2000 or later, so groups survive - see PieceGroupService.
            dest.DxfOut(path, 16, DwgVersion.Current);
        }
    }

    /// <summary>Erase exactly what was sent, for the drawing that comes back to replace.</summary>
    public static int Erase(Transaction tr, IEnumerable<ObjectId> ids)
    {
        int erased = 0;
        foreach (ObjectId id in ids)
        {
            if (id.IsErased) continue;
            ((Entity)tr.GetObject(id, OpenMode.ForWrite)).Erase();
            erased++;
        }
        return erased;
    }
}
