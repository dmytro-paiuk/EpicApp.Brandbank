using System.Text.Json;
using EpicApp.Brandbank.Api.Models;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Reads a GetNext/GetLast payload without binding it to a fixed schema. The product schema is
/// versioned by Brandbank (v1.0.2 at the time of writing), so the POC walks the JSON instead of
/// assuming a shape, which keeps it working when the feed is configured for a different version.
/// </summary>
public class PayloadInspector
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".webp", ".bmp"];

    private static readonly string[] ShotTypeKeys = ["shottype", "shottypename", "imagetype", "type", "shot"];

    private static readonly string[] FormatKeys = ["format", "filetype", "imageformat", "extension", "mimetype"];

    /// <summary>Property names that hold the actual link once we know we are inside an image object.</summary>
    private static readonly string[] LinkKeys = ["href", "url", "src", "uri"];

    /// <summary>Counts top-level products. A batch is an array; a single product comes back as an object.</summary>
    public int CountProducts(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return 0;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.Array => doc.RootElement.GetArrayLength(),
                JsonValueKind.Object => CountProductsInObject(doc.RootElement),
                _ => 0
            };
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int CountProductsInObject(JsonElement root)
    {
        // Some feeds wrap the batch in an envelope such as { "products": [ ... ] }.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array &&
                property.Name.Contains("product", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.GetArrayLength();
            }
        }

        return 1;
    }

    /// <summary>
    /// Collects every image URL in the payload along with the JSON path it sits at, so an operator
    /// can see which shot types the feed actually delivers. URLs are leased for 15 days.
    /// </summary>
    public List<ImageReference> ExtractImages(string body)
    {
        var found = new List<ImageReference>();

        if (string.IsNullOrWhiteSpace(body))
        {
            return found;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            Walk(doc.RootElement, "$", propertyName: null, shotType: null, format: null, inImageContext: false, found);
        }
        catch (JsonException)
        {
            return found;
        }

        return found
            .GroupBy(i => i.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static void Walk(JsonElement element, string path, string? propertyName, string? shotType, string? format, bool inImageContext, List<ImageReference> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Shot type and format sit beside the URL rather than on it - in the live schema the
                // link is nested one level down at images[n].url.href - so read them before descending
                // and carry them into the children.
                var childShotType = ReadLabel(element, ShotTypeKeys) ?? shotType;
                var childFormat = ReadLabel(element, FormatKeys) ?? format;
                var childInImage = inImageContext || DescribesAnImage(element);

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.{property.Name}", property.Name, childShotType, childFormat,
                        childInImage || NameSuggestsImage(property.Name), found);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index++}]", propertyName, shotType, format, inImageContext, found);
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (LooksLikeImageUrl(value, propertyName, inImageContext))
                {
                    found.Add(new ImageReference(value!, path, shotType, format));
                }

                break;
        }
    }

    /// <summary>An object is an image when it carries a shot type or an image mime type.</summary>
    private static bool DescribesAnImage(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.Replace("_", string.Empty).Replace("-", string.Empty);

            if (name.StartsWith("shottype", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name.Equals("mimetype", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString()?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static bool NameSuggestsImage(string name) =>
        name.Contains("image", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase);

    /// <summary>Picks the first scalar sibling whose name matches one of the candidate keys.</summary>
    private static string? ReadLabel(JsonElement element, string[] candidateKeys)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                continue;
            }

            var name = property.Name.Replace("_", string.Empty).Replace("-", string.Empty);
            if (candidateKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }

    /// <summary>
    /// Accepts a URL whose path ends in an image extension, one held under an image-ish property
    /// name, or a plain link inside an image object. Live leased URLs look like
    /// assets.brandbank.com/stream/downloadbound/{guid} and carry no extension at all, so the
    /// surrounding context is what identifies them.
    /// </summary>
    private static bool LooksLikeImageUrl(string? value, string? propertyName, bool inImageContext)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        if (ImageExtensions.Any(ext => uri.AbsolutePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (propertyName is not null && NameSuggestsImage(propertyName))
        {
            return true;
        }

        return inImageContext &&
               propertyName is not null &&
               LinkKeys.Contains(propertyName, StringComparer.OrdinalIgnoreCase);
    }
}
