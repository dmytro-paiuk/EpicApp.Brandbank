var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// The front-end is plain HTML, CSS and JavaScript. This host exists so the page can be run and
// published like the other projects; it holds no Brandbank logic and never sees the API key.
// Everything it needs is in wwwroot, including the appsettings.json that names the API.
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
