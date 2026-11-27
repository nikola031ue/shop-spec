// Shop application (spec 003). One image serves the API under /api and the Angular app from /.
using Shop.Api;
using Shop.Api.Auth;
using Shop.Api.Endpoints;
using Shop.Api.Payments;
using Shop.Api.Storage;
using Shop.Api.Storage.Postgres;
using Shop.Api.Storage.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => Settings.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IShopStore>(sp =>
{
    var settings = sp.GetRequiredService<Settings>();
    return settings.PostgresConnectionString is { } postgres
        ? new PostgresShopStore(postgres)
        : new RedisShopStore(settings.Redis!);
});
builder.Services.AddHostedService<StoreInitializer>();
builder.Services.AddSingleton<ExpiryJob>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExpiryJob>());

// Spec 005: a second listener for the operator's internal API, only when INTERNAL_TOKEN is set.
builder.WebHost.ConfigureKestrel(InternalEndpoints.ConfigureListeners);
builder.Services.AddProblemDetails();
builder.Services.AddAdminAuth();
builder.Services.AddOrderRateLimit();
builder.Services.AddTrustedProxies();

var app = builder.Build();

// The internal listener serves only /internal (spec 005 contracts/shop-internal-api.md).
app.UseInternalListenerScope();

// First, so everything after it (rate limits, lockout, cookies) sees the real client (spec 004 FR-022).
app.UseTrustedForwardedHeaders();
app.UseSecurityHeaders();
app.UseCookiePolicy();
app.UseShopErrorHandling();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// For local checks; the operator's readiness probe is TCP (contracts/http-api.md "Operational").
app.MapGet("/healthz", async (IShopStore store, CancellationToken ct) =>
{
    await store.PingAsync(ct);
    return Results.Ok(new { status = "ok" });
});

app.MapPublicEndpoints();
app.MapAdminEndpoints();
app.MapInternalEndpoints();

// Unknown API paths are 404s; every other path is a client route of the Angular app.
app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
