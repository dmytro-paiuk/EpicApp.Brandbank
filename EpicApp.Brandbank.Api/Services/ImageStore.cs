using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Models;
using Microsoft.Extensions.Options;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Where product images live once they have been pulled off Brandbank's 15-day leases.
///
/// Azure Blob Storage when a connection string is configured, otherwise the local disk so the POC
/// still runs on a developer machine. The rest of the app does not care which is in use.
/// </summary>
public class ImageStore
{
    private readonly BlobContainerClient? _container;
    private readonly string _localRoot;
    private readonly ILogger<ImageStore> _logger;
    private bool _containerReady;

    public ImageStore(IOptions<BrandbankOptions> options, IConfiguration configuration, IWebHostEnvironment env, ILogger<ImageStore> logger)
    {
        _logger = logger;
        _localRoot = Path.Combine(env.ContentRootPath, options.Value.ImageStorePath);

        var connectionString = configuration.GetConnectionString("BrandbankStorage");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            _container = new BlobServiceClient(connectionString)
                .GetBlobContainerClient(options.Value.BlobContainer);
        }
    }

    public bool UsingBlobStorage => _container is not null;

    public string Description => _container is not null
        ? $"Azure Blob Storage container '{_container.Name}'"
        : $"local disk ({_localRoot})";

    /// <summary>
    /// Stores one image. The name groups blobs by barcode, so everything for a product sits together
    /// and re-downloading the same shot type overwrites rather than accumulating copies.
    /// </summary>
    public async Task<StoredImage> SaveAsync(string gtin, string shotType, byte[] bytes, string contentType, CancellationToken ct)
    {
        var name = $"{gtin}/{Sanitise(shotType)}{ExtensionFor(contentType)}";

        if (_container is null)
        {
            Directory.CreateDirectory(_localRoot);
            var file = name.Replace('/', '_');
            await File.WriteAllBytesAsync(Path.Combine(_localRoot, file), bytes, ct);
            return new StoredImage(file, file, bytes.Length);
        }

        await EnsureContainerAsync(ct);

        var blob = _container.GetBlobClient(name);
        using var stream = new MemoryStream(bytes);

        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        }, ct);

        _logger.LogInformation("Stored {Blob} ({Bytes} bytes)", name, bytes.Length);

        return new StoredImage(name, blob.Uri.ToString(), bytes.Length);
    }

    public async Task<IReadOnlyList<StoredImageInfo>> ListAsync(CancellationToken ct)
    {
        if (_container is null)
        {
            return Directory.Exists(_localRoot)
                ? Directory.EnumerateFiles(_localRoot)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Select(f => new StoredImageInfo(f.Name, f.Length, f.CreationTimeUtc))
                    .ToList()
                : [];
        }

        await EnsureContainerAsync(ct);

        var blobs = new List<StoredImageInfo>();

        await foreach (var blob in _container.GetBlobsAsync(cancellationToken: ct))
        {
            blobs.Add(new StoredImageInfo(
                blob.Name,
                blob.Properties.ContentLength ?? 0,
                blob.Properties.CreatedOn?.UtcDateTime ?? DateTime.MinValue));
        }

        return blobs.OrderByDescending(b => b.CapturedUtc).ToList();
    }

    /// <summary>
    /// Streams an image back through the API. The container stays private, so nothing is publicly
    /// readable without a signed URL.
    /// </summary>
    public async Task<(Stream Content, string ContentType)?> OpenAsync(string name, CancellationToken ct)
    {
        if (_container is null)
        {
            var path = ResolveLocal(name);
            return path is null
                ? null
                : (File.OpenRead(path), ContentTypeFor(Path.GetExtension(path)));
        }

        await EnsureContainerAsync(ct);

        try
        {
            var blob = _container.GetBlobClient(name);
            var response = await blob.DownloadStreamingAsync(cancellationToken: ct);
            return (response.Value.Content, response.Value.Details.ContentType ?? "application/octet-stream");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady || _container is null)
        {
            return;
        }

        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        _containerReady = true;
    }

    /// <summary>Guards against a name being used to read outside the image folder.</summary>
    private string? ResolveLocal(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains("..") || Path.IsPathRooted(name))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(_localRoot, Path.GetFileName(name)));
        return full.StartsWith(Path.GetFullPath(_localRoot), StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    private static string Sanitise(string value)
    {
        var cleaned = string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        return string.IsNullOrWhiteSpace(cleaned) ? "image" : cleaned.Trim('-').ToLowerInvariant();
    }

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/tiff" => ".tif",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg"
    };

    public static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };
}

public record StoredImage(string Name, string Url, long Bytes);
