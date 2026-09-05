namespace EpicApp.Brandbank.Api.Configuration;

/// <summary>
/// Settings for the NielsenIQ Brandbank JSON Syndication API.
/// API keys are part of the endpoint path, so they must never be sent to the browser.
/// </summary>
public class BrandbankOptions
{
    public const string SectionName = "Brandbank";

    /// <summary>Root of the syndication API. Endpoints are appended as api/{key}, api/getlast/{key}, etc.</summary>
    public string BaseUrl { get; set; } = "https://connectapi.brandbank.com/";

    /// <summary>Brandbank normally issues a development and a live feed; each has its own key.</summary>
    public List<BrandbankFeedOptions> Feeds { get; set; } = [];

    /// <summary>Where raw GetNext/GetLast payloads are written for review.</summary>
    public string SampleStorePath { get; set; } = "App_Data/samples";

    /// <summary>Where images pulled from the 15-day leased URLs are written.</summary>
    public string ImageStorePath { get; set; } = "App_Data/images";

    /// <summary>Products per GetNext call. 1 by default; up to 1000 per the API docs.</summary>
    public int DefaultProductBatchSize { get; set; } = 1;

    /// <summary>Doc: "If after ten retries, please contact Brandbank".</summary>
    public int MaxGetLastRetries { get; set; } = 10;

    /// <summary>Pause between GetLast attempts so the last batch has time to be written.</summary>
    public int GetLastRetryDelaySeconds { get; set; } = 5;

    /// <summary>Coverage payload ceiling before the API returns 413.</summary>
    public long MaxCoverageBytes { get; set; } = 105L * 1024 * 1024;

    public int HttpTimeoutSeconds { get; set; } = 300;

    public BrandbankFeedOptions? FindFeed(string? name) =>
        Feeds.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

public class BrandbankFeedOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Supplied by Brandbank. Keep this in user-secrets or an environment variable, not in appsettings.json.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Safe-to-display fragment so an operator can tell which key is loaded without exposing it.</summary>
    public string MaskedKey => !HasKey
        ? "(not configured)"
        : ApiKey.Length <= 8
            ? new string('*', ApiKey.Length)
            : $"{ApiKey[..4]}{new string('*', Math.Min(12, ApiKey.Length - 8))}{ApiKey[^4..]}";
}
