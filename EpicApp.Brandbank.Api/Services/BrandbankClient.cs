using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Models;
using Microsoft.Extensions.Options;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Thin transport over the Brandbank endpoints. It returns the raw body and status for every call
/// so the POC page can show exactly what came back, including non-success responses.
/// </summary>
public class BrandbankClient(HttpClient http, IOptions<BrandbankOptions> options, ILogger<BrandbankClient> logger)
{
    private readonly BrandbankOptions _options = options.Value;

    /// <summary>GET api/{key}[?products=n] — the next queued product batch.</summary>
    public Task<BrandbankCallResult> GetNextAsync(BrandbankFeedOptions feed, int? products, CancellationToken ct)
    {
        var path = $"api/{feed.ApiKey}";
        if (products is > 0)
        {
            path += $"?products={products}";
        }

        return SendAsync(HttpMethod.Get, path, content: null, ct);
    }

    /// <summary>GET api/getlast/{key} — replays the batch the last GetNext produced.</summary>
    public Task<BrandbankCallResult> GetLastAsync(BrandbankFeedOptions feed, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"api/getlast/{feed.ApiKey}", content: null, ct);

    /// <summary>POST api/resend/{key} — requeues up to 20 products by pvid or gtin.</summary>
    public Task<BrandbankCallResult> ResendAsync(BrandbankFeedOptions feed, string json, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/resend/{feed.ApiKey}", JsonContent(json), ct);

    /// <summary>POST api/receiveCoverage/{key} — replaces the range list for the feed.</summary>
    public Task<BrandbankCallResult> UploadCoverageAsync(BrandbankFeedOptions feed, string json, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/receiveCoverage/{feed.ApiKey}", JsonContent(json), ct);

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    private async Task<BrandbankCallResult> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var uri = new Uri(new Uri(_options.BaseUrl), path);
        var safeUrl = Redact(uri.ToString());
        var sw = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(method, uri);
        request.Content = content;

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            logger.LogInformation("Brandbank {Method} {Url} -> {Status} in {Elapsed}ms ({Bytes} bytes)",
                method, safeUrl, (int)response.StatusCode, sw.ElapsedMilliseconds, body.Length);

            var (valid, error) = ValidateJson(body, (int)response.StatusCode);

            return new BrandbankCallResult(
                (int)response.StatusCode,
                response.ReasonPhrase ?? string.Empty,
                body,
                valid,
                error,
                sw.ElapsedMilliseconds,
                safeUrl);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // A timeout leaves the batch written to the GetLast location, so this is recoverable.
            sw.Stop();
            logger.LogWarning(ex, "Brandbank {Method} {Url} timed out after {Elapsed}ms", method, safeUrl, sw.ElapsedMilliseconds);
            return new BrandbankCallResult(0, "Timeout", string.Empty, false, "The request timed out before a complete response arrived.", sw.ElapsedMilliseconds, safeUrl);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "Brandbank {Method} {Url} failed", method, safeUrl);
            return new BrandbankCallResult(0, "Transport error", string.Empty, false, ex.Message, sw.ElapsedMilliseconds, safeUrl);
        }
    }

    /// <summary>
    /// The documented failure mode is a truncated body, so every payload is parsed before it is trusted.
    /// </summary>
    private static (bool Valid, string? Error) ValidateJson(string body, int status)
    {
        if (status == 204 || string.IsNullOrWhiteSpace(body))
        {
            return (true, null);
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
            return (true, null);
        }
        catch (JsonException ex)
        {
            return (false, $"Payload is not complete JSON: {ex.Message}");
        }
    }

    /// <summary>Keeps the API key out of logs and out of anything echoed back to the page.</summary>
    private string Redact(string url)
    {
        foreach (var feed in _options.Feeds.Where(f => f.HasKey))
        {
            url = url.Replace(feed.ApiKey, "{apiKey}", StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }
}
