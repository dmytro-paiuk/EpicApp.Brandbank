namespace EpicApp.Brandbank.Api.Models;

/// <summary>Feed descriptor sent to the test page. Carries the masked key only, never the real one.</summary>
public record FeedInfo(string Name, bool HasKey, string MaskedKey);

public record GetNextRequest(string Feed, int? Products);

public record GetLastRequest(string Feed);

public record ResendRequest(string Feed, List<ResendRequestItem> Items);

/// <summary>
/// AllowInvalidGtins must be set explicitly to upload a file whose barcodes fail validation - Brandbank
/// would accept it and then match nothing.
/// </summary>
public record CoverageUploadRequest(string Feed, string CoverageJson, bool AllowInvalidGtins = false);

public record CoverageValidateRequest(string CoverageJson);

/// <summary>
/// Outcome of checking a coverage file's barcodes. FixedCoverageJson is the same file with check
/// digits restored, returned only when there is something to fix; every other field is preserved.
/// </summary>
public record CoverageValidationResult(
    int Products,
    int Gtins,
    int Valid,
    int Invalid,
    bool LikelyMissingCheckDigits,
    string Summary,
    List<GtinIssue> Issues,
    int TotalIssues,
    int FixedCount,
    string? FixedCoverageJson);

/// <summary>Row is 1-based to match how people count products in the file.</summary>
public record GtinIssue(int Row, string? RetailerId, string? Description, string Gtin, string Problem, string? Suggested);

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
