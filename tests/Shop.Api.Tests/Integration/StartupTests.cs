using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Auth;
using Shop.Api.Storage;

namespace Shop.Api.Tests.Integration;

/// <summary>The app with the operator's environment variables set, as a pod would get them.</summary>
public sealed class ShopApp(IReadOnlyDictionary<string, string?> env) : WebApplicationFactory<Program>
{
    public const string AdminPassword = "generated-admin-password";

    public static Dictionary<string, string?> BaseEnv() => new()
    {
        ["SHOP_NAME"] = "Acme",
        ["WALLET_ADDRESS"] = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
        ["ADMIN_PASSWORD"] = AdminPassword,
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in env)
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>Checkpoint for Phase 3: the app initialises its store and starts on either database.</summary>
public abstract class StartupContract
{
    protected abstract Task<Dictionary<string, string?>> EnvAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartsInitialisesAndAnswersHealth()
    {
        await using var app = new ShopApp(await EnvAsync());
        using var client = app.CreateClient();

        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Ct);
        using var unknownApi = await client.GetAsync(new Uri("/api/nope", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
        Assert.Equal("application/problem+json", unknownApi.Content.Headers.ContentType?.MediaType);

        var store = app.Services.GetRequiredService<IShopStore>();
        var admin = await store.GetAdminAsync(Ct);
        Assert.NotNull(admin);
        Assert.True(admin.Initial);
        var hasher = app.Services.GetRequiredService<IPasswordHasher<AdminUser>>();
        Assert.NotEqual(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(AdminUser.Instance, admin.PasswordHash, ShopApp.AdminPassword));
    }

    [Fact]
    public async Task SecondReplicaSharesTheStore()
    {
        var env = await EnvAsync();
        await using var first = new ShopApp(env);
        await using var second = new ShopApp(env);
        first.CreateClient().Dispose();
        second.CreateClient().Dispose();

        var a = await first.Services.GetRequiredService<IShopStore>().GetAdminAsync(Ct);
        var b = await second.Services.GetRequiredService<IShopStore>().GetAdminAsync(Ct);

        Assert.Equal(a, b);
    }
}

public sealed class PostgresStartupTests(PostgresContainer db) : StartupContract, IClassFixture<PostgresContainer>
{
    protected override async Task<Dictionary<string, string?>> EnvAsync()
    {
        var env = ShopApp.BaseEnv();
        env["DATABASE_URL"] = await db.CreateDatabaseAsync("shop_" + Guid.NewGuid().ToString("N"));
        return env;
    }
}

public sealed class RedisStartupTests(RedisContainer redis) : StartupContract, IClassFixture<RedisContainer>
{
    protected override Task<Dictionary<string, string?>> EnvAsync()
    {
        var env = ShopApp.BaseEnv();
        env["REDIS_URL"] = redis.Url;
        env["REDIS_PASSWORD"] = redis.Password;
        return Task.FromResult(env);
    }
}

public sealed class InvalidConfigurationTests
{
    [Fact]
    public void MissingConfigurationStopsTheStartAndNamesTheVariables()
    {
        var env = ShopApp.BaseEnv();
        env.Remove("WALLET_ADDRESS");
        using var app = new ShopApp(env);

        var ex = Assert.Throws<SettingsException>(() => app.CreateClient());

        Assert.Contains("WALLET_ADDRESS", ex.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one of DATABASE_URL or REDIS_URL", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ShopApp.AdminPassword, ex.Message, StringComparison.Ordinal);
    }
}
