using System.Collections.Generic;
using Newtonsoft.Json;

namespace autodraw_plugin.Models.AutoDraw;

public class AutoDrawMetaDTO
{
    public int current_step { get; set; }
    public int current_substep { get; set; }
    public bool is_complete { get; set; }
    public string last_updated { get; set; }
}

public class AutoDrawConfigDTO
{
    public int stepCount { get; set; }
    public List<ConfigStepDTO> steps { get; set; } = new();
}

public class ConfigStepDTO
{
    public string key { get; set; }
    public string label { get; set; }
    public List<ShowRuleDTO> show { get; set; } = new();
    public List<ConfigSubstepDTO> substeps { get; set; } = new();
}

public class ShowRuleDTO
{
    public string query { get; set; }
    public string value { get; set; }
}

public class ConfigSubstepDTO
{
    public string key { get; set; }
    public string label { get; set; }
    public string method { get; set; }
    public bool automated { get; set; }
}

public class AutoDrawRecordDTO
{
    public string created_at { get; set; }
    public List<GeometryItemDTO> geometry { get; set; } = new();
}

public class GeometryItemDTO
{
    public string id { get; set; }
    public string key { get; set; }
    public string ad_layer { get; set; }
    public int product_index { get; set; }
    public List<string> tags { get; set; }
    public string type { get; set; }
    
    // For LineItem - simplified for DTO (could use inheritance/converter if needed)
    public LineAttributesDTO attributes { get; set; }
}

public class LineAttributesDTO
{
    public double[] start { get; set; }
    public double[] end { get; set; }
}