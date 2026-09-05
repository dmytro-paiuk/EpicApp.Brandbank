using EpicApp.Brandbank.Api.Configuration;
using EpicApp.Brandbank.Api.Endpoints;
using EpicApp.Brandbank.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<BrandbankOptions>(builder.Configuration.GetSection(BrandbankOptions.SectionName));

var options = builder.Configuration.GetSection(BrandbankOptions.SectionName).Get<BrandbankOptions>() ?? new BrandbankOptions();

// The web front-end is deployed separately, so it calls this API cross-origin.
// Origins are listed explicitly - a wildcard would let any page on the internet drive the feed.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

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
builder.Services.AddScoped<BrandbankFeedService>();

var app = builder.Build();

app.UseCors();

app.MapBrandbankEndpoints();

// Lets the deployment be smoke-tested without touching the feed.
app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.Run();
