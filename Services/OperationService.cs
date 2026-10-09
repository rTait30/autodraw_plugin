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
    public static async Task<OperationResultDTO> Run(string id, string dxfPath, JObject inputs,
                                                     int objectFloor)
    {
        var fields = new Dictionary<string, string>
        {
            // Pieces come back numbered above every piece already in the
            // drawing, so none is grouped with one it is not part of.
            ["object_floor"] = objectFloor.ToString(),
            ["inputs"] = inputs.ToString(Formatting.None),
            // The designer's layout, as every automation call carries it:
            // replace hands the drawing back where it was, stack beside it.
            ["layout"] = PluginSettings.Current.Layout.ToLowerInvariant(),
            ["direction"] = PluginSettings.Current.Direction.ToLowerInvariant(),
        };
        if (DataProjectId.HasValue) fields["project_id"] = DataProjectId.Value.ToString();
        // The states along the way, as deep as the designer asked to see them.
        int depth = PluginSettings.Current.StepsDepth;
        if (depth != 0) fields["steps_depth"] = depth < 0 ? "all" : depth.ToString();
        HttpResponseMessage response = await ApiService.PostDxf(
            $"/operations/{Uri.EscapeDataString(id)}/run", dxfPath, fields);
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
    /// The project ADDO runs on, when one is chosen (ADDOPROJECT); a free run
    /// on the drawing as it is otherwise.
    /// </summary>
    public static int? ProjectId { get; set; }

    /// <summary>
    /// The project a free run takes its answers from (ADDODATA), storing
    /// nothing on it; none, and everything not in the drawing is asked.
    /// </summary>
    public static int? DataProjectId { get; set; }

    /// <summary>The project drawing last laid in here, which a run starts from.</summary>
    public static int? CurrentArtifact { get; set; }

    /// <summary>
    /// Whether the drawing on screen is the project's as ADDO laid it in. Only
    /// then is it sent back with a run: a drawing laid in some other way is in
    /// another place on the page and would come back looking moved.
    /// </summary>
    public static bool Loaded { get; set; }

    private static Dictionary<string, string> LaidOut() => new()
    {
        ["layout"] = PluginSettings.Current.Layout.ToLowerInvariant(),
        ["direction"] = PluginSettings.Current.Direction.ToLowerInvariant(),
    };

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
    /// readable rather than thrown.
    /// </summary>
    public static async Task<OperationResultDTO> RunOnProject(
        int projectId, string id, string? dxfPath, JObject inputs, int? baseArtifact)
    {
        var fields = LaidOut();
        fields["inputs"] = inputs.ToString(Formatting.None);
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
