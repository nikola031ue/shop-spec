using Npgsql;

namespace Shop.Api.Tests.Unit;

public sealed class SettingsTests
{
    private const string DbSecret = "db-S3cret!pw";
    private const string AdminSecret = "admin-Secret-value";
    private const string RedisSecret = "redis-Secret-value";

    private static Dictionary<string, string?> Postgres() => new()
    {
        ["SHOP_NAME"] = "Acme",
        ["WALLET_ADDRESS"] = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
        ["ADMIN_PASSWORD"] = AdminSecret,
        ["DATABASE_URL"] = $"postgresql://app:{Uri.EscapeDataString(DbSecret)}@acme-db-rw.shops.svc:5432/app",
    };

    private static Dictionary<string, string?> Redis() => new()
    {
        ["SHOP_NAME"] = "Acme",
        ["WALLET_ADDRESS"] = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
        ["ADMIN_PASSWORD"] = AdminSecret,
        ["REDIS_URL"] = "redis://acme-redis.shops.svc:6379",
        ["REDIS_PASSWORD"] = RedisSecret,
    };

    private static Settings Load(Dictionary<string, string?> env) => Settings.Load(k => env.GetValueOrDefault(k));

    [Fact]
    public void PostgresUriBecomesNpgsqlConnectionString()
    {
        var settings = Load(Postgres());

        var cs = new NpgsqlConnectionStringBuilder(settings.PostgresConnectionString);
        Assert.Equal("acme-db-rw.shops.svc", cs.Host);
        Assert.Equal(5432, cs.Port);
        Assert.Equal("app", cs.Database);
        Assert.Equal("app", cs.Username);
        Assert.Equal(DbSecret, cs.Password);
        Assert.Equal(GssEncryptionMode.Disable, cs.GssEncryptionMode);
        Assert.Null(settings.Redis);
        Assert.Equal("Acme", settings.ShopName);
    }

    [Theory]
    [InlineData("postgresql://u:p@h/db?sslmode=require", Npgsql.SslMode.Require)]
    [InlineData("postgres://u:p@h:6543/db?sslmode=verify-full", Npgsql.SslMode.VerifyFull)]
    public void PostgresUriOptions(string uri, Npgsql.SslMode mode)
    {
        var cs = new NpgsqlConnectionStringBuilder(Settings.ToNpgsql(uri));

        Assert.Equal(mode, cs.SslMode);
    }

    [Fact]
    public void RedisSettingsUsePasswordAndDoNotAbortOnConnectFail()
    {
        var settings = Load(Redis());

        Assert.NotNull(settings.Redis);
        Assert.Equal(RedisSecret, settings.Redis.Password);
        Assert.False(settings.Redis.AbortOnConnectFail);
        Assert.Equal("acme-redis.shops.svc:6379", settings.Redis.EndPoints.Single().ToString()!.Replace("Unspecified/", "", StringComparison.Ordinal));
        Assert.Null(settings.PostgresConnectionString);
    }

    [Theory]
    [InlineData("SHOP_NAME")]
    [InlineData("WALLET_ADDRESS")]
    [InlineData("ADMIN_PASSWORD")]
    public void MissingRequiredVariableIsNamed(string name)
    {
        var env = Postgres();
        env.Remove(name);

        var ex = Assert.Throws<SettingsException>(() => Load(env));

        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        AssertNoSecrets(ex.Message);
    }

    [Fact]
    public void BothOrNeitherDatabaseIsRejected()
    {
        var both = Postgres();
        both["REDIS_URL"] = "redis://r:6379";
        both["REDIS_PASSWORD"] = RedisSecret;
        var neither = Postgres();
        neither.Remove("DATABASE_URL");

        Assert.Contains("exactly one of DATABASE_URL or REDIS_URL", Assert.Throws<SettingsException>(() => Load(both)).Message, StringComparison.Ordinal);
        Assert.Contains("exactly one of DATABASE_URL or REDIS_URL", Assert.Throws<SettingsException>(() => Load(neither)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedisNeedsPassword()
    {
        var env = Redis();
        env.Remove("REDIS_PASSWORD");

        var ex = Assert.Throws<SettingsException>(() => Load(env));

        Assert.Contains("REDIS_PASSWORD", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mysql://u:db-S3cret!pw@h/db")]
    [InlineData("postgresql://u:db-S3cret!pw@h")]
    [InlineData("not a uri db-S3cret!pw")]
    public void InvalidDatabaseUrlIsNamedWithoutItsValue(string url)
    {
        var env = Postgres();
        env["DATABASE_URL"] = url;

        var ex = Assert.Throws<SettingsException>(() => Load(env));

        Assert.Contains("DATABASE_URL", ex.Message, StringComparison.Ordinal);
        AssertNoSecrets(ex.Message);
    }

    [Fact]
    public void InvalidRedisUrlIsNamedWithoutItsValue()
    {
        var env = Redis();
        env["REDIS_URL"] = "http://redis-secret-host:6379";

        var ex = Assert.Throws<SettingsException>(() => Load(env));

        Assert.Contains("REDIS_URL", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("redis-secret-host", ex.Message, StringComparison.Ordinal);
        AssertNoSecrets(ex.Message);
    }

    private static void AssertNoSecrets(string message)
    {
        Assert.DoesNotContain(DbSecret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminSecret, message, StringComparison.Ordinal);
        Assert.DoesNotContain(RedisSecret, message, StringComparison.Ordinal);
    }

    // ---- Spec 005 settings (all optional, so 0.3.0 runs under an operator that doesn't set them) ----

    [Fact]
    public void PaymentSettingsDefaultToOffAndSepolia()
    {
        var settings = Load(Postgres());

        Assert.False(settings.PaymentsEnabled);
        Assert.Null(settings.TokenAddress);
        Assert.Null(settings.InternalToken);
        Assert.Equal((11155111L, "https://sepolia.etherscan.io/tx/", 30, 8081, 8080),
            (settings.ChainId, settings.ExplorerTxUrl, settings.OrderPaymentMinutes, settings.InternalPort, settings.PublicPort));
    }

    [Fact]
    public void PaymentSettingsAreReadAndTheTokenAddressChecksummed()
    {
        var env = Postgres();
        env["TOKEN_ADDRESS"] = "0xdf8d5deb736ebc79f7e99c4c90975d4b8f8d80e2";
        env["CHAIN_ID"] = "31337";
        env["EXPLORER_TX_URL"] = "https://explorer.example/tx/";
        env["ORDER_PAYMENT_MINUTES"] = "45";
        env["INTERNAL_TOKEN"] = new string('k', 44);
        env["INTERNAL_PORT"] = "9091";
        env["HTTP_PORTS"] = "9090;9095";

        var settings = Load(env);

        Assert.True(settings.PaymentsEnabled);
        Assert.Equal("0xdF8d5dEb736EBC79f7e99C4c90975d4b8f8d80E2", settings.TokenAddress);
        Assert.Equal((31337L, "https://explorer.example/tx/", 45, 9091, 9090),
            (settings.ChainId, settings.ExplorerTxUrl, settings.OrderPaymentMinutes, settings.InternalPort, settings.PublicPort));
    }

    [Theory]
    [InlineData("TOKEN_ADDRESS", "0xnot-an-address")]
    [InlineData("TOKEN_ADDRESS", "0xdF8d5dEb736EBC79f7e99C4c90975d4b8f8d80e2")] // checksum typo
    [InlineData("CHAIN_ID", "zero")]
    [InlineData("ORDER_PAYMENT_MINUTES", "-5")]
    [InlineData("ORDER_PAYMENT_MINUTES", "1441")]
    [InlineData("EXPLORER_TX_URL", "http://insecure.example/tx/")]
    [InlineData("INTERNAL_TOKEN", "too-short")]
    [InlineData("INTERNAL_PORT", "70000")]
    public void InvalidPaymentSettingsNameTheVariableNotTheValue(string name, string value)
    {
        var env = Postgres();
        env[name] = value;

        var error = Assert.Throws<SettingsException>(() => Load(env));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInternalPortMustDifferFromThePublicPort()
    {
        var env = Postgres();
        env["INTERNAL_TOKEN"] = new string('k', 44);
        env["INTERNAL_PORT"] = "8080";

        Assert.Contains("INTERNAL_PORT", Assert.Throws<SettingsException>(() => Load(env)).Message, StringComparison.Ordinal);
    }
}
