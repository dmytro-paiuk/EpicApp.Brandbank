using System.Text.Json;
using System.Text.Json.Nodes;
using EpicApp.Brandbank.Api.Models;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>
/// Checks the barcodes in a coverage file before it is uploaded. Brandbank accepts a file with
/// unmatchable GTINs and answers 200, then silently queues nothing, so this is the only point at
/// which a bad barcode can be caught.
/// </summary>
public class CoverageValidator
{
    private const int MaxGtinLength = 14;
    private const int MinGtinLength = 8;

    /// <summary>
    /// A number with a dropped check digit still passes the check about one time in ten, purely by
    /// chance. So when most of a file fails, the whole file is treated as missing its check digits,
    /// including the rows that happened to pass.
    /// </summary>
    private const double SystematicFailureShare = 0.5;

    private const int MaxIssuesReturned = 500;

    public CoverageValidationResult Validate(string coverageJson)
    {
        var products = Parse(coverageJson);

        var gtins = products
            .SelectMany((product, index) => ReadGtins(product).Select(node => (Index: index, Product: product, Node: node)))
            .ToList();

        var checks = gtins
            .Select(g => (g.Index, g.Product, g.Node, Value: g.Node["gtin"]?.GetValue<string>() ?? string.Empty))
            .Select(g => (g.Index, g.Product, g.Node, g.Value, Problem: Problem(g.Value)))
            .ToList();

        var invalid = checks.Count(c => c.Problem is not null);
        var systematic = checks.Count > 0 && invalid >= checks.Count * SystematicFailureShare;

        var issues = new List<GtinIssue>();
        var fixedCount = 0;

        foreach (var check in checks)
        {
            string? suggested = null;
            string? problem = check.Problem;

            if (systematic)
            {
                suggested = AddCheckDigit(check.Value);

                // A row that passed is only listed when its corrected value differs, which it will
                // whenever the passing was a coincidence.
                if (problem is null && suggested is not null && suggested != Normalise(check.Value))
                {
                    problem = "Passes the check digit by chance. The rest of this file is missing check digits, so this barcode almost certainly is too.";
                }
            }
            else if (problem is not null && IsDigits(check.Value) && check.Value.Length is >= MinGtinLength and <= MaxGtinLength)
            {
                problem += $" Expected {ExpectedCheckDigit(check.Value)} as the last digit. Check the barcode on the package - a typo cannot be corrected automatically.";
            }

            if (problem is null)
            {
                continue;
            }

            if (suggested is not null)
            {
                check.Node["gtin"] = suggested;
                fixedCount++;
            }

            issues.Add(new GtinIssue(
                check.Index + 1,
                Read(check.Product, "retailerId"),
                Read(check.Product, "description"),
                check.Value,
                problem,
                suggested));
        }

        issues.AddRange(Duplicates(checks.Select(c => (c.Index, c.Product, Value: c.Node["gtin"]?.GetValue<string>() ?? string.Empty))));

        return new CoverageValidationResult(
            Products: products.Count,
            Gtins: checks.Count,
            Valid: checks.Count - invalid,
            Invalid: invalid,
            LikelyMissingCheckDigits: systematic,
            Summary: Summarise(products.Count, checks.Count, invalid, systematic, issues.Count),
            Issues: issues.Take(MaxIssuesReturned).ToList(),
            TotalIssues: issues.Count,
            FixedCount: fixedCount,
            FixedCoverageJson: fixedCount > 0 ? products.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null);
    }

    /// <summary>GS1 mod-10: digits are weighted 3 and 1 alternately, starting from the right of the body.</summary>
    public static int CheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var digit = body[body.Length - 1 - i] - '0';
            sum += digit * (i % 2 == 0 ? 3 : 1);
        }

        return (10 - sum % 10) % 10;
    }

    public static bool IsValidGtin(string value) => Problem(value) is null;

    private static string? Problem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Barcode is empty.";
        }

        if (!IsDigits(value))
        {
            return "Barcode contains characters other than digits.";
        }

        if (value.Length > MaxGtinLength)
        {
            return $"Barcode is {value.Length} digits; a GTIN is at most {MaxGtinLength}.";
        }

        if (value.TrimStart('0').Length < MinGtinLength - 1)
        {
            return "Barcode is too short to be a GTIN.";
        }

        return CheckDigit(value[..^1]) == value[^1] - '0'
            ? null
            : "Check digit is wrong.";
    }

    /// <summary>Treats the value as a barcode with its check digit dropped and restores it, padded to 14 digits.</summary>
    private static string? AddCheckDigit(string value)
    {
        if (!IsDigits(value))
        {
            return null;
        }

        var body = value.TrimStart('0');
        if (body.Length is 0 || body.Length >= MaxGtinLength)
        {
            return null;
        }

        return (body + CheckDigit(body)).PadLeft(MaxGtinLength, '0');
    }

    private static int ExpectedCheckDigit(string value) => CheckDigit(value[..^1]);

    private static string Normalise(string value) => IsDigits(value) ? value.PadLeft(MaxGtinLength, '0') : value;

    private static bool IsDigits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    private static JsonArray Parse(string coverageJson)
    {
        if (string.IsNullOrWhiteSpace(coverageJson))
        {
            throw new InvalidOperationException("The coverage payload is empty.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(coverageJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The coverage payload is not valid JSON: {ex.Message}");
        }

        return root as JsonArray
               ?? throw new InvalidOperationException("Coverage must be a JSON array of products.");
    }

    private static IEnumerable<JsonObject> ReadGtins(JsonNode? product) =>
        product?["gtins"] is JsonArray gtins
            ? gtins.OfType<JsonObject>().Where(g => g["gtin"] is JsonValue value && value.TryGetValue<string>(out _))
            : [];

    private static string? Read(JsonNode? product, string property) =>
        product?[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static IEnumerable<GtinIssue> Duplicates(IEnumerable<(int Index, JsonNode? Product, string Value)> gtins) =>
        gtins
            .Where(g => IsDigits(g.Value))
            .GroupBy(g => g.Value.TrimStart('0'))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1).Select(g => new GtinIssue(
                g.Index + 1,
                Read(g.Product, "retailerId"),
                Read(g.Product, "description"),
                g.Value,
                $"Duplicate barcode - also on row {group.First().Index + 1}.",
                null)));

    private static string Summarise(int products, int gtins, int invalid, bool systematic, int issues)
    {
        if (gtins == 0)
        {
            return $"{products} product(s) but no barcodes were found. Each product needs gtins[].gtin.";
        }

        if (issues == 0)
        {
            return $"All {gtins} barcode(s) across {products} product(s) are valid.";
        }

        if (systematic)
        {
            return $"{invalid} of {gtins} barcodes fail the check digit. That is far more than chance, so this file's "
                   + "barcodes are missing their check digit - a common export from point-of-sale systems. Brandbank "
                   + "would accept the upload but match nothing. Fixing adds the check digit to every barcode.";
        }

        return $"{issues} barcode problem(s) in {gtins} barcode(s). Brandbank would accept the upload but cannot match these.";
    }
}
