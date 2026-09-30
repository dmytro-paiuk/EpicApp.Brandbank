using System.Text.Json.Nodes;
using EpicApp.Brandbank.Api.Models;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Pulls images from the leased URLs in a payload into permanent storage. The lease lasts 15 days
/// and a 404 usually means it has expired or that shot type is not available for the product.
/// </summary>
public class ImageDownloader(
    HttpClient http,
    ImageStore store,
    ProductStore products,
    ILogger<ImageDownloader> logger)
{
    public string StorageDescription => store.Description;

    /// <summary>
    /// Downloads every image in a payload, filing each under its product and shot type. Called as
    /// soon as a batch arrives, because the URLs are leases and the queue does not hand them out twice.
    /// </summary>
    public async Task<List<ImageDownloadResult>> DownloadPayloadAsync(string body, CancellationToken ct)
    {
        var results = new List<ImageDownloadResult>();

        foreach (var product in ProductStore.EnumerateProducts(body))
        {
            var gtin = ProductStore.NormaliseGtin(Text(product, "gtin")) ?? "unknown";

            if (product["images"] is not JsonArray images)
            {
                continue;
            }

            foreach (var image in images.OfType<JsonObject>())
            {
                var url = Text(image["url"], "href");

                if (!string.IsNullOrWhiteSpace(url))
                {
                    results.Add(await DownloadOneAsync(url, gtin, Text(image, "shotType") ?? "image", ct));
                }
            }
        }

        return results;
    }

    /// <summary>Downloads a loose list of URLs, used when re-fetching images for a saved payload.</summary>
    public async Task<List<ImageDownloadResult>> DownloadAsync(IEnumerable<string> urls, string gtin, CancellationToken ct)
    {
        var results = new List<ImageDownloadResult>();

        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await DownloadOneAsync(url, gtin, "image", ct));
        }

        return results;
    }

    private async Task<ImageDownloadResult> DownloadOneAsync(string url, string gtin, string shotType, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ImageDownloadResult(url, false, 0, null, 0, null, "Not an absolute http(s) URL.");
        }

        try
        {
            using var response = await http.GetAsync(uri, ct);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var reason = status == 404
                    ? "404 - the 15 day lease has expired, or no image exists for this shot type."
                    : $"{status} {response.ReasonPhrase}";

                return new ImageDownloadResult(url, false, status, null, 0, null, reason);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            var stored = await store.SaveAsync(gtin, shotType, bytes, contentType, ct);
            await products.MarkImageStoredAsync(url, stored.Name, stored.Url, stored.Bytes, ct);

            logger.LogInformation("Stored {ShotType} for {Gtin} ({Bytes} bytes)", shotType, gtin, stored.Bytes);

            return new ImageDownloadResult(url, true, status, stored.Name, stored.Bytes, contentType, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Image download failed for {Path}", uri.AbsolutePath);
            return new ImageDownloadResult(url, false, 0, null, 0, null, ex.Message);
        }
    }

    public Task<IReadOnlyList<StoredImageInfo>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public Task<(Stream Content, string ContentType)?> OpenAsync(string name, CancellationToken ct) =>
        store.OpenAsync(name, ct);

    private static string? Text(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
