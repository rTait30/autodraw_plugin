using System.Collections.Generic;
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
        string name = id.Split('@')[0];
        HttpResponseMessage response = await ApiService.PostDxf($"/operations/{name}/run", dxfPath, fields);
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
