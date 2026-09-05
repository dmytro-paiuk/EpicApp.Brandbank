using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Models;
using Microsoft.Extensions.Options;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Writes each payload to disk so it can be reviewed later. Brandbank is not an on-demand service:
/// whatever GetNext hands over is the only copy, so it is stored before anything else touches it.
/// </summary>
public class PayloadStore(IOptions<BrandbankOptions> options, IWebHostEnvironment env, ILogger<PayloadStore> logger)
{
    private readonly BrandbankOptions _options = options.Value;

    private string Root => Path.Combine(env.ContentRootPath, _options.SampleStorePath);

    public async Task<SampleInfo> SaveAsync(string feed, string endpoint, string body, int productCount, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);

        var capturedUtc = DateTime.UtcNow;
        var id = $"{capturedUtc:yyyyMMdd-HHmmss}-{endpoint.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..6]}";
        var file = $"{id}.json";

        await File.WriteAllTextAsync(Path.Combine(Root, file), body, ct);

        var info = new SampleInfo(id, file, endpoint, feed, capturedUtc, Encoding.UTF8.GetByteCount(body), productCount);
        await File.WriteAllTextAsync(
            Path.Combine(Root, $"{id}.meta.json"),
            JsonSerializer.Serialize(info, PocJson.Web),
            ct);

        logger.LogInformation("Saved {Endpoint} sample {Id} for feed {Feed} ({Bytes} bytes, {Products} products)",
            endpoint, id, feed, info.Bytes, productCount);

        return info;
    }

    public IReadOnlyList<SampleInfo> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var samples = new List<SampleInfo>();

        foreach (var meta in Directory.EnumerateFiles(Root, "*.meta.json"))
        {
            try
            {
                var info = JsonSerializer.Deserialize<SampleInfo>(File.ReadAllText(meta), PocJson.Web);
                if (info is not null)
                {
                    samples.Add(info);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Skipping unreadable sample metadata {File}", meta);
            }
        }

        return samples.OrderByDescending(s => s.CapturedUtc).ToList();
    }

    public async Task<string?> ReadAsync(string id, CancellationToken ct)
    {
        var path = ResolvePath(id);
        return path is null ? null : await File.ReadAllTextAsync(path, ct);
    }

    /// <summary>Guards against an id being used to walk out of the sample folder.</summary>
    private string? ResolvePath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..") || Path.IsPathRooted(id))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(Root, $"{Path.GetFileName(id)}.json"));
        return full.StartsWith(Path.GetFullPath(Root), StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public static string Hash(string body) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}
