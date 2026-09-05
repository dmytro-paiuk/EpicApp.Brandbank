namespace EpicApp.Brandbank.Api.Models;

/// <summary>Feed descriptor sent to the test page. Carries the masked key only, never the real one.</summary>
public record FeedInfo(string Name, bool HasKey, string MaskedKey);

public record GetNextRequest(string Feed, int? Products);

public record GetLastRequest(string Feed);

public record ResendRequest(string Feed, List<ResendRequestItem> Items);

public record CoverageUploadRequest(string Feed, string CoverageJson);

public record ImageDownloadRequest(string? SampleId, List<string> Urls);

/// <summary>Raw outcome of a single call to Brandbank.</summary>
public record BrandbankCallResult(
    int StatusCode,
    string ReasonPhrase,
    string Body,
    bool IsValidJson,
    string? JsonError,
    long ElapsedMs,
    string Url);

/// <summary>What the test page shows after a GetNext or GetLast run.</summary>
public record FeedFetchResult(
    string Feed,
    string Endpoint,
    int StatusCode,
    string Outcome,
    string Message,
    long ElapsedMs,
    int ProductCount,
    string? SampleId,
    string? SampleFile,
    long PayloadBytes,
    string? PayloadHash,
    int Attempts,
    List<ImageReference> Images,
    string? Preview);

/// <summary>An image URL lifted out of a product payload, with the JSON path it came from.</summary>
public record ImageReference(string Url, string Path, string? ShotType, string? Format);

public record ImageDownloadResult(
    string Url,
    bool Success,
    int StatusCode,
    string? File,
    long Bytes,
    string? ContentType,
    string? Error);

public record SampleInfo(string Id, string File, string Endpoint, string Feed, DateTime CapturedUtc, long Bytes, int ProductCount);

public record StoredImageInfo(string File, long Bytes, DateTime CapturedUtc);
