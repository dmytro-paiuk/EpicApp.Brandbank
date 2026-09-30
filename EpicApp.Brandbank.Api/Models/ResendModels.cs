using System.Text.Json.Serialization;

namespace EpicApp.Brandbank.Api.Models;

/// <summary>
/// Resend request item. Send either a pvid or a gtin, never both.
/// Max 20 items per request, one request per 30 seconds.
/// </summary>
public class ResendRequestItem
{
    [JsonPropertyName("pvid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pvid { get; set; }

    [JsonPropertyName("gtin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Gtin { get; set; }
}
