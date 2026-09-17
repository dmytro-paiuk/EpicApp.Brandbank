using Microsoft.AspNetCore.Diagnostics;
using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Endpoints;
using EpicApp.Brandbank.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BrandbankOptions>(builder.Configuration.GetSection(BrandbankOptions.SectionName));

var options = builder.Configuration.GetSection(BrandbankOptions.SectionName).Get<BrandbankOptions>() ?? new BrandbankOptions();

// The web front-end is deployed separately, so it calls this API cross-origin.
// Origins are listed explicitly - a wildcard would let any page on the internet drive the feed.
// Trailing slashes are trimmed because an Origin header never carries a path - a configured
// "https://host/" would silently match nothing and every call would fail as a CORS error.
var allowedOrigins = (builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .Select(origin => origin.TrimEnd('/'))
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .ToArray();

builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
    }
}));

// The API key travels in the URL path, so every Brandbank call is made from the server.
// The browser only ever talks to this API.
builder.Services.AddHttpClient<BrandbankClient>(http =>
{
    http.BaseAddress = new Uri(options.BaseUrl);
    http.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
    http.DefaultRequestHeaders.Add("Accept", "application/json");
})
// The default HttpClient logger writes the request URI verbatim, which would put the API key in
// the logs. BrandbankClient logs every call itself with the key redacted, so drop these loggers.
.RemoveAllLoggers();

builder.Services.AddHttpClient<ImageDownloader>(http =>
{
    http.Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds);
});

builder.Services.AddSingleton<PayloadStore>();
builder.Services.AddSingleton<PayloadInspector>();
builder.Services.AddSingleton<CoverageValidator>();
builder.Services.AddScoped<BrandbankFeedService>();

var app = builder.Build();

// Runs before UseCors so that a failure response still carries the CORS headers. Without this an
// unhandled exception returns a bare 500 that the browser refuses to read, and the page reports the
// API as unreachable when it is actually running and returning an error.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<IExceptionHandlerFeature>();
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

    logger.LogError(feature?.Error, "Unhandled exception for {Method} {Path}",
        context.Request.Method, context.Request.Path);

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/json";

    await context.Response.WriteAsJsonAsync(new
    {
        message = feature?.Error.Message ?? "The request failed.",
        type = feature?.Error.GetType().Name
    });
}));

app.UseCors();

app.MapBrandbankEndpoints();

// Lets the deployment be smoke-tested without touching the feed.
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();
