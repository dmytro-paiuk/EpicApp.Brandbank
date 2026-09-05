using System.Text.Json;

namespace EpicApp.Brandbank.Api.Services;

/// <summary>Shared serializer settings. Named to avoid clashing with Microsoft.AspNetCore.Mvc.JsonOptions.</summary>
public static class PocJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
