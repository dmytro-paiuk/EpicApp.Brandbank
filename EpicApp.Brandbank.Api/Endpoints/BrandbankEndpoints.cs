using System.Text.Json;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Models;
using EpicApp.Brandbank.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EpicApp.Brandbank.Api.Endpoints;

/// <summary>
/// The endpoints the POC page calls. Everything Brandbank-facing happens here, server side,
/// so the feed API key never reaches the browser.
/// </summary>
public static class BrandbankEndpoints
{
    public static void MapBrandbankEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/brandbank");

        group.MapGet("/feeds", (IOptions<BrandbankOptions> options) =>
            Results.Ok(options.Value.Feeds
                .Select(f => new FeedInfo(f.Name, f.HasKey, f.MaskedKey))
                .ToList()));

        group.MapPost("/getnext", async (
            GetNextRequest request,
            IOptions<BrandbankOptions> options,
            BrandbankFeedService service,
            CancellationToken ct) =>
        {
            var feed = Resolve(options.Value, request.Feed, out var error);
            return feed is null
                ? Results.BadRequest(new { message = error })
                : Results.Ok(await service.GetNextAsync(feed, request.Products, ct));
        });

        group.MapPost("/getlast", async (
            GetLastRequest request,
            IOptions<BrandbankOptions> options,
            BrandbankFeedService service,
            CancellationToken ct) =>
        {
            var feed = Resolve(options.Value, request.Feed, out var error);
            return feed is null
                ? Results.BadRequest(new { message = error })
                : Results.Ok(await service.GetLastAsync(feed, ct));
        });

        group.MapPost("/resend", async (
            ResendRequest request,
            IOptions<BrandbankOptions> options,
            BrandbankFeedService service,
            CancellationToken ct) =>
        {
            var feed = Resolve(options.Value, request.Feed, out var error);
            if (feed is null)
            {
                return Results.BadRequest(new { message = error });
            }

            try
            {
                var result = await service.ResendAsync(feed, request.Items ?? [], ct);
                return Results.Ok(new
                {
                    result.StatusCode,
                    Message = BrandbankFeedService.DescribeResendStatus(result.StatusCode),
                    result.Body,
                    result.ElapsedMs
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        group.MapPost("/coverage", async (
            CoverageUploadRequest request,
            IOptions<BrandbankOptions> options,
            BrandbankFeedService service,
            CancellationToken ct) =>
        {
            var feed = Resolve(options.Value, request.Feed, out var error);
            if (feed is null)
            {
                return Results.BadRequest(new { message = error });
            }

            try
            {
                var result = await service.UploadCoverageAsync(feed, request.CoverageJson, ct);
                return Results.Ok(new
                {
                    result.StatusCode,
                    Message = BrandbankFeedService.DescribeCoverageStatus(result.StatusCode),
                    result.Body,
                    result.ElapsedMs
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        group.MapGet("/samples", (PayloadStore store) => Results.Ok(store.List()));

        group.MapGet("/samples/{id}", async (string id, PayloadStore store, CancellationToken ct) =>
        {
            var body = await store.ReadAsync(id, ct);
            return body is null
                ? Results.NotFound(new { message = $"Sample '{id}' was not found." })
                : Results.Text(body, "application/json");
        });

        group.MapGet("/samples/{id}/images", async (string id, PayloadStore store, PayloadInspector inspector, CancellationToken ct) =>
        {
            var body = await store.ReadAsync(id, ct);
            return body is null
                ? Results.NotFound(new { message = $"Sample '{id}' was not found." })
                : Results.Ok(inspector.ExtractImages(body));
        });

        group.MapPost("/images/download", async (
            ImageDownloadRequest request,
            ImageDownloader downloader,
            PayloadStore store,
            PayloadInspector inspector,
            CancellationToken ct) =>
        {
            var urls = request.Urls ?? [];

            // With no explicit list, take every image URL in the named sample.
            if (urls.Count == 0 && !string.IsNullOrWhiteSpace(request.SampleId))
            {
                var body = await store.ReadAsync(request.SampleId, ct);
                if (body is null)
                {
                    return Results.NotFound(new { message = $"Sample '{request.SampleId}' was not found." });
                }

                urls = inspector.ExtractImages(body).Select(i => i.Url).ToList();
            }

            return urls.Count == 0
                ? Results.BadRequest(new { message = "No image URLs to download." })
                : Results.Ok(await downloader.DownloadAsync(urls, request.SampleId, ct));
        });

        group.MapGet("/images", (ImageDownloader downloader) => Results.Ok(downloader.List()));

        group.MapGet("/images/{file}", (string file, ImageDownloader downloader) =>
        {
            var path = downloader.ResolvePath(file);
            return path is null
                ? Results.NotFound()
                : Results.File(path, ContentTypeFor(path));
        });

        // Builds a valid coverage body from the documented fields so the upload can be tried end to end.
        group.MapPost("/coverage/sample", ([FromBody] List<CoverageProduct>? products) =>
        {
            var payload = products is { Count: > 0 } ? products : SampleCoverage();
            return Results.Text(JsonSerializer.Serialize(payload, PocJson.Indented), "application/json");
        });
    }

    private static BrandbankFeedOptions? Resolve(BrandbankOptions options, string? name, out string? error)
    {
        var feed = options.FindFeed(name);

        if (feed is null)
        {
            error = $"Feed '{name}' is not configured. Configured feeds: {string.Join(", ", options.Feeds.Select(f => f.Name))}.";
            return null;
        }

        if (!feed.HasKey)
        {
            error = $"Feed '{feed.Name}' has no API key. Set Brandbank:Feeds:<index>:ApiKey via user-secrets or an environment variable.";
            return null;
        }

        error = null;
        return feed;
    }

    private static List<CoverageProduct> SampleCoverage() =>
    [
        new()
        {
            OwnLabel = false,
            Description = "UNKNOWN",
            RetailerId = "8008698016930",
            Gtins = [new CoverageGtin { Gtin = "8008698016930", Supplier = "UNKNOWN", Preferred = true }]
        }
    ];

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}
