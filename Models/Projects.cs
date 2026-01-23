using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

using autodraw_plugin.Models.AutoDraw;

namespace autodraw_plugin.Models.Projects;

// ROOT PROJECT
public class ProjectDetailsDTO
{
    public JToken? project_attributes { get; set; }

    // REPLACES: product_attributes
    public List<ProjectProductDTO> products { get; set; } = new();

    public AutoDrawConfigDTO autodraw_config { get; set; } = null!;
    public AutoDrawMetaDTO autodraw_meta { get; set; } = null!;
    public AutoDrawRecordDTO autodraw_record { get; set; } = null!;
}

public class ProjectProductDTO
{
    public int item_index { get; set; }          // stable ordering / index
    public string label { get; set; } = "";      // e.g. "Sail A", "Cover 1"
    public JToken? attributes { get; set; }      // per-product type-specific JSON blob
}

public class ProductDTO

{
    public int id { get; set; }
    public string name { get; set; }

    public string info()
    {
        return "Product ID: " + id + " (" + name + ")";
    }
}

public class ProjectGeneralInfoDTO
{
    public string client_id { get; set; }
    public string client_name { get; set; }

    public string name { get; set; }

    public string info()
    {
        return "Project name: " + name + " | Client: " + client_id + " (" + client_name + ")";
    }

}