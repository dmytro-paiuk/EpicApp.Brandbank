using System.Security.Cryptography;
using System.Text;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Models;
using Microsoft.Extensions.Options;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Pulls images from the leased URLs in a payload into local storage. The lease lasts 15 days and
/// a 404 usually means it has expired or that shot type is not available for the product.
/// </summary>
public class ImageDownloader(HttpClient http, IOptions<BrandbankOptions> options, IWebHostEnvironment env, ILogger<ImageDownloader> logger)
{
    private readonly BrandbankOptions _options = options.Value;

    private string Root => Path.Combine(env.ContentRootPath, _options.ImageStorePath);

    public async Task<List<ImageDownloadResult>> DownloadAsync(IEnumerable<string> urls, string? sampleId, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);

        var results = new List<ImageDownloadResult>();

        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await DownloadOneAsync(url, sampleId, ct));
        }

        return results;
    }

    private async Task<ImageDownloadResult> DownloadOneAsync(string url, string? sampleId, CancellationToken ct)
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
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var file = BuildFileName(uri, sampleId, contentType);

            await File.WriteAllBytesAsync(Path.Combine(Root, file), bytes, ct);

            logger.LogInformation("Downloaded image {File} ({Bytes} bytes, {ContentType})", file, bytes.Length, contentType);

            return new ImageDownloadResult(url, true, status, file, bytes.Length, contentType, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Image download failed for {Url}", uri.AbsolutePath);
            return new ImageDownloadResult(url, false, 0, null, 0, null, ex.Message);
        }
    }

    /// <summary>
    /// Names the file from the URL path plus a hash of the full URL, so two leases for different
    /// shot types with the same file name cannot overwrite each other.
    /// </summary>
    private static string BuildFileName(Uri uri, string? sampleId, string? contentType)
    {
        var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "image";
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = contentType switch
            {
                "image/png" => ".png",
                "image/tiff" => ".tif",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".jpg"
            };
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString())))[..8];
        var prefix = string.IsNullOrWhiteSpace(sampleId) ? string.Empty : $"{Sanitize(sampleId)}_";

        return $"{prefix}{Sanitize(name)}_{hash}{extension}";
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public IReadOnlyList<StoredImageInfo> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return Directory.EnumerateFiles(Root)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new StoredImageInfo(f.Name, f.Length, f.CreationTimeUtc))
            .ToList();
    }

    /// <summary>Resolves a stored image for preview, refusing anything that escapes the image folder.</summary>
    public string? ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || Path.IsPathRooted(fileName))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(Root, Path.GetFileName(fileName)));
        return full.StartsWith(Path.GetFullPath(Root), StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }
}
