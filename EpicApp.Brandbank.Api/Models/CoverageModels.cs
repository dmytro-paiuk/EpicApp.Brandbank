using System.Text.Json.Serialization;

namespace EpicApp.Brandbank.Api.Models;

/// <summary>
/// Coverage ("range list") item, per the JSON Syndication API field table.
/// Each upload overwrites the previous one, so the file must contain every product every time.
/// </summary>
public class CoverageProduct
{
    /// <summary>Mandatory. True for own-label products, false for branded. Usually defaults to false.</summary>
    [JsonPropertyName("ownLabel")]
    public bool OwnLabel { get; set; }

    /// <summary>Mandatory. Product name, max 100 characters. Defaults to "UNKNOWN" when not held.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "UNKNOWN";

    /// <summary>Mandatory. Retailer's internal product id, max 20 characters. Falls back to the GTIN.</summary>
    [JsonPropertyName("retailerId")]
    public string RetailerId { get; set; } = string.Empty;

    /// <summary>Optional. Date the product is due to go live in the client's system.</summary>
    [JsonPropertyName("rangedOnlineDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? RangedOnlineDate { get; set; }

    /// <summary>Mandatory. GTIN/supplier pairs used to match products.</summary>
    [JsonPropertyName("gtins")]
    public List<CoverageGtin> Gtins { get; set; } = [];

    /// <summary>Optional, 1 to 4 levels. All-or-nothing: do not mix products with and without categories.</summary>
    [JsonPropertyName("categories")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CoverageCategory>? Categories { get; set; }

    /// <summary>Optional free-form extras that help the Brandbank retail team source products.</summary>
    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<CoverageExtension>? Extensions { get; set; }
}

public class CoverageGtin
{
    /// <summary>Mandatory. GTIN/EAN/barcode; leading zeros are matched loosely up to 14 digits.</summary>
    [JsonPropertyName("gtin")]
    public string Gtin { get; set; } = string.Empty;

    /// <summary>Mandatory. Supplier name, defaults to "UNKNOWN" when not held.</summary>
    [JsonPropertyName("supplier")]
    public string Supplier { get; set; } = "UNKNOWN";

    /// <summary>Optional. Marks the preferred GTIN when several map to one retailer id.</summary>
    [JsonPropertyName("preferred")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Preferred { get; set; }
}

public class CoverageCategory
{
    /// <summary>1 (broadest) to 4 (most specific).</summary>
    [JsonPropertyName("level")]
    public int Level { get; set; }

    /// <summary>Unique code for the category. One code must never map to two category names.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public class CoverageExtension
{
    /// <summary>Max 200 characters.</summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>Max 200 characters.</summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
