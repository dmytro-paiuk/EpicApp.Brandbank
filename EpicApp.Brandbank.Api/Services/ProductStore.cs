using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using EpicApp.Brandbank.Api.Models;
using Microsoft.Data.SqlClient;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Holds the current version of every product the feed has delivered, keyed by GTIN: update the row
/// when the barcode is already known, insert it when it is not.
///
/// Azure SQL. The tables are created on first use so there is no migration step to run by hand.
/// </summary>
public class ProductStore(IConfiguration configuration, ILogger<ProductStore> logger)
{
    private readonly string? _connectionString = configuration.GetConnectionString("BrandbankDb");
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _ready;

    /// <summary>The API still runs without a database so the feed can be exercised; nothing is stored.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_connectionString);

    private const string Schema = """
        IF OBJECT_ID('dbo.BrandbankProducts', 'U') IS NULL
        CREATE TABLE dbo.BrandbankProducts (
            -- The same product arrives as 12, 13 or 14 digits depending on the message, so the key is
            -- always padded to 14. Without that one product would occupy several rows.
            Gtin                 CHAR(14)       NOT NULL PRIMARY KEY,
            GtinAsSent           VARCHAR(20)    NOT NULL,
            Pvid                 VARCHAR(50)    NULL,
            Pid                  VARCHAR(50)    NULL,
            BrandbankCode        VARCHAR(50)    NULL,
            Description          NVARCHAR(400)  NULL,
            -- 'Delete' means the supplier discontinued or replaced the product. The row is kept:
            -- there may still be stock on the shelf.
            UpdateType           VARCHAR(50)    NULL,
            DefaultLanguage      VARCHAR(20)    NULL,
            TargetMarkets        VARCHAR(200)   NULL,
            Eu1169Compliance     VARCHAR(50)    NULL,
            RangeItemId          NVARCHAR(200)  NULL,
            CategoriesJson       NVARCHAR(MAX)  NULL,
            VersionDateTime      DATETIME2      NULL,
            FileCreationDateTime DATETIME2      NULL,
            PayloadJson          NVARCHAR(MAX)  NOT NULL,
            SampleId             VARCHAR(100)   NULL,
            FirstSeenUtc         DATETIME2      NOT NULL,
            LastUpdatedUtc       DATETIME2      NOT NULL
        );

        IF OBJECT_ID('dbo.BrandbankProductImages', 'U') IS NULL
        CREATE TABLE dbo.BrandbankProductImages (
            Gtin           CHAR(14)       NOT NULL,
            ShotTypeId     INT            NOT NULL,
            ShotType       NVARCHAR(100)  NULL,
            MimeType       VARCHAR(100)   NULL,
            Width          INT            NULL,
            Height         INT            NULL,
            -- Brandbank's hash of the image. It changes only when the picture changes, so it tells us
            -- whether the stored copy needs replacing.
            Thumbprint     VARCHAR(100)   NULL,
            -- Leased for 15 days: a way to fetch the file, never a way to serve it.
            SourceUrl      NVARCHAR(1000) NULL,
            LeaseExpires   DATETIME2      NULL,
            BlobName       NVARCHAR(400)  NULL,
            BlobUrl        NVARCHAR(1000) NULL,
            SizeBytes      BIGINT         NULL,
            StoredUtc      DATETIME2      NULL,
            LastUpdatedUtc DATETIME2      NOT NULL,
            CONSTRAINT PK_BrandbankProductImages PRIMARY KEY (Gtin, ShotTypeId),
            CONSTRAINT FK_BrandbankProductImages_Products FOREIGN KEY (Gtin)
                REFERENCES dbo.BrandbankProducts (Gtin) ON DELETE CASCADE
        );

        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_BrandbankProductImages_SourceUrl')
            CREATE INDEX IX_BrandbankProductImages_SourceUrl
                ON dbo.BrandbankProductImages (SourceUrl);
        """;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        if (_ready)
        {
            return connection;
        }

        await _schemaLock.WaitAsync(ct);

        try
        {
            if (!_ready)
            {
                await using var create = connection.CreateCommand();
                create.CommandText = Schema;
                await create.ExecuteNonQueryAsync(ct);
                _ready = true;
                logger.LogInformation("Brandbank tables ready on {Database}", connection.Database);
            }
        }
        finally
        {
            _schemaLock.Release();
        }

        return connection;
    }

    /// <summary>Upserts every product in a GetNext/GetLast payload and returns what changed.</summary>
    public async Task<ProductUpsertResult> SavePayloadAsync(string body, string? sampleId, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new ProductUpsertResult(0, 0, 0, 0);
        }

        var products = EnumerateProducts(body).ToList();
        if (products.Count == 0)
        {
            return new ProductUpsertResult(0, 0, 0, 0);
        }

        await using var connection = await OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var inserted = 0;
        var updated = 0;
        var skipped = 0;
        var images = 0;

        foreach (var product in products)
        {
            var gtin = NormaliseGtin(Text(product, "gtin"));
            if (gtin is null)
            {
                skipped++;
                continue;
            }

            var outcome = await UpsertProductAsync(connection, transaction, product, gtin, sampleId, ct);

            if (outcome == "INSERT") inserted++;
            else if (outcome == "UPDATE") updated++;
            else skipped++;

            if (outcome != "STALE")
            {
                images += await UpsertImagesAsync(connection, transaction, product, gtin, ct);
            }
        }

        await transaction.CommitAsync(ct);

        logger.LogInformation("Stored payload: {Inserted} new, {Updated} updated, {Skipped} skipped, {Images} image rows",
            inserted, updated, skipped, images);

        return new ProductUpsertResult(inserted, updated, skipped, images);
    }

    private static async Task<string> UpsertProductAsync(
        SqlConnection connection, SqlTransaction transaction, JsonNode product, string gtin, string? sampleId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            """
            MERGE dbo.BrandbankProducts AS target
            USING (SELECT @Gtin AS Gtin) AS source ON target.Gtin = source.Gtin
            -- Payloads can arrive out of order, so an older version must never overwrite a newer row.
            WHEN MATCHED AND (@VersionDateTime IS NULL
                           OR target.VersionDateTime IS NULL
                           OR @VersionDateTime >= target.VersionDateTime) THEN
                UPDATE SET
                    GtinAsSent = @GtinAsSent, Pvid = @Pvid, Pid = @Pid, BrandbankCode = @BrandbankCode,
                    Description = @Description, UpdateType = @UpdateType, DefaultLanguage = @DefaultLanguage,
                    TargetMarkets = @TargetMarkets, Eu1169Compliance = @Eu1169Compliance,
                    RangeItemId = @RangeItemId, CategoriesJson = @CategoriesJson,
                    VersionDateTime = @VersionDateTime, FileCreationDateTime = @FileCreationDateTime,
                    PayloadJson = @PayloadJson, SampleId = @SampleId, LastUpdatedUtc = @Now
            WHEN NOT MATCHED THEN
                INSERT (Gtin, GtinAsSent, Pvid, Pid, BrandbankCode, Description, UpdateType,
                        DefaultLanguage, TargetMarkets, Eu1169Compliance, RangeItemId, CategoriesJson,
                        VersionDateTime, FileCreationDateTime, PayloadJson, SampleId,
                        FirstSeenUtc, LastUpdatedUtc)
                VALUES (@Gtin, @GtinAsSent, @Pvid, @Pid, @BrandbankCode, @Description, @UpdateType,
                        @DefaultLanguage, @TargetMarkets, @Eu1169Compliance, @RangeItemId, @CategoriesJson,
                        @VersionDateTime, @FileCreationDateTime, @PayloadJson, @SampleId,
                        @Now, @Now)
            OUTPUT $action;
            """, connection, transaction);

        command.Parameters.AddWithValue("@Gtin", gtin);
        command.Parameters.AddWithValue("@GtinAsSent", Text(product, "gtin") ?? gtin);
        command.Parameters.AddWithValue("@Pvid", Value(Text(product, "pvid")));
        command.Parameters.AddWithValue("@Pid", Value(Text(product, "pid")));
        command.Parameters.AddWithValue("@BrandbankCode", Value(Text(product, "brandbankCode")));
        command.Parameters.AddWithValue("@Description", Value(Text(product, "description")));
        command.Parameters.AddWithValue("@UpdateType", Value(Text(product, "updateType")));
        command.Parameters.AddWithValue("@DefaultLanguage", Value(Text(product, "defaultLanguage")));
        command.Parameters.AddWithValue("@TargetMarkets", Value(Join(product["targetMarkets"])));
        command.Parameters.AddWithValue("@Eu1169Compliance", Value(Text(product, "eu1169Compliance")));
        command.Parameters.AddWithValue("@RangeItemId", Value(Text(product, "range_item_id")));
        command.Parameters.AddWithValue("@CategoriesJson", Value(product["categories"]?.ToJsonString()));
        command.Parameters.AddWithValue("@VersionDateTime", Value(Date(Text(product, "versionDateTime"))));
        command.Parameters.AddWithValue("@FileCreationDateTime", Value(Date(Text(product, "fileCreationDateTime"))));
        command.Parameters.AddWithValue("@PayloadJson", product.ToJsonString());
        command.Parameters.AddWithValue("@SampleId", Value(sampleId));
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);

        // No row is output when the MERGE matched but the version guard rejected the update.
        var action = await command.ExecuteScalarAsync(ct);
        return action as string ?? "STALE";
    }

    private static async Task<int> UpsertImagesAsync(
        SqlConnection connection, SqlTransaction transaction, JsonNode product, string gtin, CancellationToken ct)
    {
        if (product["images"] is not JsonArray images)
        {
            return 0;
        }

        var count = 0;

        foreach (var image in images.OfType<JsonObject>())
        {
            await using var command = new SqlCommand(
                """
                MERGE dbo.BrandbankProductImages AS target
                USING (SELECT @Gtin AS Gtin, @ShotTypeId AS ShotTypeId) AS source
                    ON target.Gtin = source.Gtin AND target.ShotTypeId = source.ShotTypeId
                WHEN MATCHED THEN
                    UPDATE SET
                        ShotType = @ShotType, MimeType = @MimeType, Width = @Width, Height = @Height,
                        SourceUrl = @SourceUrl, LeaseExpires = @LeaseExpires, LastUpdatedUtc = @Now,
                        -- A changed thumbprint means a different picture, so the stored copy is stale.
                        BlobName  = CASE WHEN ISNULL(target.Thumbprint, '') = ISNULL(@Thumbprint, '')
                                         THEN target.BlobName ELSE NULL END,
                        BlobUrl   = CASE WHEN ISNULL(target.Thumbprint, '') = ISNULL(@Thumbprint, '')
                                         THEN target.BlobUrl ELSE NULL END,
                        StoredUtc = CASE WHEN ISNULL(target.Thumbprint, '') = ISNULL(@Thumbprint, '')
                                         THEN target.StoredUtc ELSE NULL END,
                        Thumbprint = @Thumbprint
                WHEN NOT MATCHED THEN
                    INSERT (Gtin, ShotTypeId, ShotType, MimeType, Width, Height,
                            Thumbprint, SourceUrl, LeaseExpires, LastUpdatedUtc)
                    VALUES (@Gtin, @ShotTypeId, @ShotType, @MimeType, @Width, @Height,
                            @Thumbprint, @SourceUrl, @LeaseExpires, @Now);
                """, connection, transaction);

            command.Parameters.AddWithValue("@Gtin", gtin);
            command.Parameters.AddWithValue("@ShotTypeId", Number(image["shotTypeId"]) ?? count);
            command.Parameters.AddWithValue("@ShotType", Value(Text(image, "shotType")));
            command.Parameters.AddWithValue("@MimeType", Value(Text(image, "mimeType")));
            command.Parameters.AddWithValue("@Width", Value(Number(image["width"])));
            command.Parameters.AddWithValue("@Height", Value(Number(image["height"])));
            command.Parameters.AddWithValue("@Thumbprint", Value(Text(image, "thumbprint")));
            command.Parameters.AddWithValue("@SourceUrl", Value(Text(image["url"], "href")));
            command.Parameters.AddWithValue("@LeaseExpires", Value(Date(Text(image["url"], "expiryDateTime"))));
            command.Parameters.AddWithValue("@Now", DateTime.UtcNow);

            await command.ExecuteNonQueryAsync(ct);
            count++;
        }

        return count;
    }

    /// <summary>Records where an image ended up in blob storage, against the shot type it belongs to.</summary>
    public async Task MarkImageStoredAsync(string sourceUrl, string blobName, string blobUrl, long sizeBytes, CancellationToken ct)
    {
        if (!Enabled)
        {
            return;
        }

        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.BrandbankProductImages
            SET BlobName = @BlobName, BlobUrl = @BlobUrl, SizeBytes = @SizeBytes, StoredUtc = @Now
            WHERE SourceUrl = @SourceUrl;
            """, connection);

        command.Parameters.AddWithValue("@BlobName", blobName);
        command.Parameters.AddWithValue("@BlobUrl", blobUrl);
        command.Parameters.AddWithValue("@SizeBytes", sizeBytes);
        command.Parameters.AddWithValue("@Now", DateTime.UtcNow);
        command.Parameters.AddWithValue("@SourceUrl", sourceUrl);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Pads to 14 digits. Brandbank sends the same barcode as 12, 13 or 14 digits depending on the
    /// message, and without this the same product would occupy several rows.
    /// </summary>
    public static string? NormaliseGtin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is 0 or > 14 ? null : digits.PadLeft(14, '0');
    }

    /// <summary>A batch is an array; a single product is an object; some feeds wrap it in an envelope.</summary>
    public static IEnumerable<JsonNode> EnumerateProducts(string body)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            yield break;
        }

        switch (root)
        {
            case JsonArray array:
                foreach (var item in array.OfType<JsonObject>()) yield return item;
                break;

            case JsonObject obj when obj.FirstOrDefault(p =>
                p.Value is JsonArray && p.Key.Contains("product", StringComparison.OrdinalIgnoreCase)).Value is JsonArray nested:
                foreach (var item in nested.OfType<JsonObject>()) yield return item;
                break;

            case JsonObject single:
                yield return single;
                break;
        }
    }

    private static string? Text(JsonNode? node, string property) =>
        node?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string? Join(JsonNode? node) =>
        node is JsonArray array ? string.Join(", ", array.Select(v => v?.ToString()).Where(v => v is not null)) : null;

    private static DateTime? Date(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static object Value(object? value) => value ?? DBNull.Value;
}
