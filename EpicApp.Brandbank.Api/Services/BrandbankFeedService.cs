using System.Collections.Concurrent;
using System.Text.Json;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Models;
using Microsoft.Extensions.Options;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Implements the documented GetNext/GetLast process: fetch a batch, validate that the JSON is
/// complete, and fall back to GetLast when it is truncated or the call times out. Also enforces
/// the resend limits (20 products per request, one request every 30 seconds) before calling out.
/// </summary>
public class BrandbankFeedService(
    BrandbankClient client,
    PayloadStore store,
    PayloadInspector inspector,
    IOptions<BrandbankOptions> options,
    ILogger<BrandbankFeedService> logger)
{
    private const int ResendMaxProducts = 20;
    private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(30);

    private readonly BrandbankOptions _options = options.Value;

    /// <summary>Last accepted payload per feed, used to tell a fresh GetLast batch from a replay of one already ingested.</summary>
    private static readonly ConcurrentDictionary<string, string> LastPayloadHashes = new();

    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastResendAt = new();

    public async Task<FeedFetchResult> GetNextAsync(BrandbankFeedOptions feed, int? products, CancellationToken ct)
    {
        var batchSize = products ?? _options.DefaultProductBatchSize;
        var attempts = 1;
        var result = await client.GetNextAsync(feed, batchSize, ct);

        if (result.StatusCode == 204)
        {
            return Empty(feed.Name, "GetNext", result, "The queue is empty - every available product has been downloaded.");
        }

        if (result.StatusCode is not 200 && result.StatusCode != 0)
        {
            return Failure(feed.Name, "GetNext", result, DescribeGetStatus(result.StatusCode), attempts);
        }

        // A truncated body or a timeout is exactly what GetLast exists for.
        if (!result.IsValidJson || result.StatusCode == 0)
        {
            logger.LogWarning("GetNext for feed {Feed} did not return a complete payload ({Reason}); falling back to GetLast",
                feed.Name, result.JsonError);

            return await RecoverWithGetLastAsync(feed, attempts, ct);
        }

        return await AcceptAsync(feed.Name, "GetNext", result, attempts, ct);
    }

    public async Task<FeedFetchResult> GetLastAsync(BrandbankFeedOptions feed, CancellationToken ct)
    {
        var result = await client.GetLastAsync(feed, ct);

        if (result.StatusCode == 204)
        {
            return Empty(feed.Name, "GetLast", result, "No last batch is available for this feed.");
        }

        if (result.StatusCode is not 200)
        {
            return Failure(feed.Name, "GetLast", result, DescribeGetStatus(result.StatusCode), 1);
        }

        if (!result.IsValidJson)
        {
            return Failure(feed.Name, "GetLast", result, result.JsonError ?? "The payload is not complete JSON.", 1);
        }

        return await AcceptAsync(feed.Name, "GetLast", result, 1, ct);
    }

    /// <summary>
    /// Retries GetLast until a complete payload arrives that differs from the one already ingested.
    /// A matching payload means the last batch has not been rewritten yet, so it waits and tries again.
    /// </summary>
    private async Task<FeedFetchResult> RecoverWithGetLastAsync(BrandbankFeedOptions feed, int attempts, CancellationToken ct)
    {
        BrandbankCallResult? last = null;

        for (var retry = 1; retry <= _options.MaxGetLastRetries; retry++)
        {
            attempts++;
            await Task.Delay(TimeSpan.FromSeconds(_options.GetLastRetryDelaySeconds), ct);

            last = await client.GetLastAsync(feed, ct);

            if (last.StatusCode == 204)
            {
                return Empty(feed.Name, "GetLast", last, "GetNext failed and GetLast has no content to replay.");
            }

            if (last.StatusCode != 200 || !last.IsValidJson)
            {
                logger.LogWarning("GetLast retry {Retry}/{Max} for feed {Feed} was not usable ({Status})",
                    retry, _options.MaxGetLastRetries, feed.Name, last.StatusCode);
                continue;
            }

            var hash = PayloadStore.Hash(last.Body);
            if (LastPayloadHashes.TryGetValue(feed.Name, out var previous) && previous == hash)
            {
                logger.LogInformation("GetLast retry {Retry}/{Max} for feed {Feed} returned the batch already ingested; waiting",
                    retry, _options.MaxGetLastRetries, feed.Name);
                continue;
            }

            return await AcceptAsync(feed.Name, "GetLast", last, attempts, ct);
        }

        var exhausted = last ?? new BrandbankCallResult(0, "No response", string.Empty, false, null, 0, "api/getlast/{apiKey}");
        return Failure(
            feed.Name,
            "GetLast",
            exhausted,
            $"GetLast did not produce a new complete payload after {_options.MaxGetLastRetries} retries. Contact Brandbank support.",
            attempts);
    }

    /// <summary>Stores the payload, records its hash, and reports what it contains.</summary>
    private async Task<FeedFetchResult> AcceptAsync(string feed, string endpoint, BrandbankCallResult result, int attempts, CancellationToken ct)
    {
        var productCount = inspector.CountProducts(result.Body);
        var images = inspector.ExtractImages(result.Body);
        var sample = await store.SaveAsync(feed, endpoint, result.Body, productCount, ct);
        var hash = PayloadStore.Hash(result.Body);

        LastPayloadHashes[feed] = hash;

        return new FeedFetchResult(
            feed,
            endpoint,
            result.StatusCode,
            "Success",
            $"Received {productCount} product(s) and {images.Count} image URL(s). Call again until you get a 204.",
            result.ElapsedMs,
            productCount,
            sample.Id,
            sample.File,
            sample.Bytes,
            hash,
            attempts,
            images,
            Preview(result.Body));
    }

    private static FeedFetchResult Empty(string feed, string endpoint, BrandbankCallResult result, string message) =>
        new(feed, endpoint, result.StatusCode, "Empty", message, result.ElapsedMs, 0, null, null, 0, null, 1, [], null);

    private static FeedFetchResult Failure(string feed, string endpoint, BrandbankCallResult result, string message, int attempts) =>
        new(feed, endpoint, result.StatusCode, "Failed", message, result.ElapsedMs, 0, null, null,
            result.Body.Length, null, attempts, [], Preview(result.Body));

    private static string DescribeGetStatus(int status) => status switch
    {
        0 => "The call did not complete. Check connectivity and the host certificate chain.",
        400 => "400 Bad Request - check the API key and the products parameter.",
        401 => "401 Unauthorized - the API key was rejected.",
        500 => "500 Internal server error at Brandbank.",
        _ => $"Unexpected status {status}. Brandbank may surface codes beyond those in the documentation."
    };

    /// <summary>Prettifies the head of the payload for on-screen review; large batches stay on disk.</summary>
    private static string? Preview(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var formatted = JsonSerializer.Serialize(doc.RootElement, PocJson.Indented);
            return formatted.Length <= 200_000 ? formatted : formatted[..200_000] + "\n\n... truncated for display. The full payload is saved to disk.";
        }
        catch (JsonException)
        {
            return body.Length <= 200_000 ? body : body[..200_000] + "\n\n... truncated for display.";
        }
    }

    public async Task<BrandbankCallResult> ResendAsync(BrandbankFeedOptions feed, List<ResendRequestItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            throw new InvalidOperationException("Provide at least one pvid or gtin to resend.");
        }

        if (items.Count > ResendMaxProducts)
        {
            throw new InvalidOperationException(
                $"A maximum of {ResendMaxProducts} products is allowed per resend request; {items.Count} were supplied. " +
                "Brandbank rejects the whole request with a 400 and actions no resends.");
        }

        if (items.Any(i => string.IsNullOrWhiteSpace(i.Pvid) && string.IsNullOrWhiteSpace(i.Gtin)))
        {
            throw new InvalidOperationException("Every resend item needs either a pvid or a gtin.");
        }

        // The API allows one call every 30 seconds; blocking here avoids burning a 429.
        if (LastResendAt.TryGetValue(feed.Name, out var last))
        {
            var wait = ResendInterval - (DateTimeOffset.UtcNow - last);
            if (wait > TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"The resend endpoint allows one request every 30 seconds. Wait {Math.Ceiling(wait.TotalSeconds)} more second(s).");
            }
        }

        var json = JsonSerializer.Serialize(items, PocJson.Web);
        var result = await client.ResendAsync(feed, json, ct);
        LastResendAt[feed.Name] = DateTimeOffset.UtcNow;

        return result;
    }

    public async Task<BrandbankCallResult> UploadCoverageAsync(BrandbankFeedOptions feed, string coverageJson, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(coverageJson))
        {
            throw new InvalidOperationException("The coverage payload is empty.");
        }

        // Validate before uploading; a bad file costs a 400 and leaves the previous coverage in place.
        try
        {
            using var _ = JsonDocument.Parse(coverageJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The coverage payload is not valid JSON: {ex.Message}");
        }

        var bytes = System.Text.Encoding.UTF8.GetByteCount(coverageJson);
        if (bytes > _options.MaxCoverageBytes)
        {
            throw new InvalidOperationException(
                $"The coverage payload is {bytes / 1024 / 1024}MB. The endpoint accepts at most " +
                $"{_options.MaxCoverageBytes / 1024 / 1024}MB, above which it returns 413. Compress the body before uploading.");
        }

        return await client.UploadCoverageAsync(feed, coverageJson, ct);
    }

    public static string DescribeCoverageStatus(int status) => status switch
    {
        200 => "200 OK - coverage received.",
        400 => "400 Bad Request - the coverage failed to send. Validate it against the Brandbank coverage schema.",
        401 => "401 Unauthorized - the API key was rejected.",
        413 => "413 Request Entity Too Large - the payload exceeds 105MB. Compress the JSON body.",
        429 => "429 Too Many Requests - the previous coverage is still processing.",
        500 => "500 Internal server error at Brandbank.",
        0 => "The call did not complete. Check connectivity and the host certificate chain.",
        _ => $"Unexpected status {status}."
    };

    public static string DescribeResendStatus(int status) => status switch
    {
        202 => "202 Accepted - the products have been marked for resend and will appear on GetNext.",
        400 => "400 Bad Request - check the payload; more than 20 products in one request is rejected outright.",
        429 => "429 Too Many Requests - the 30 second rate limit is in force. The response says how long to wait.",
        500 => "500 Internal server error at Brandbank.",
        0 => "The call did not complete. Check connectivity and the host certificate chain.",
        _ => $"Unexpected status {status}."
    };
}
