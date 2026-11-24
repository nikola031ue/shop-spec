// Shop application entry point (spec 003). Feature endpoints arrive in later phases.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Client-side routes are served by the Angular app.
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
