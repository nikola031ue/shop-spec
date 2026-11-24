using Npgsql;
using StackExchange.Redis;

namespace Shop.Api;

/// <summary>
/// Configuration injected by shop-operator (contracts/operator-0.2.0.md, research R10). Errors name
/// the variable and never include its value.
/// </summary>
public sealed class Settings
{
    public required string ShopName { get; init; }
    public required string WalletAddress { get; init; }
    public required string AdminPassword { get; init; }

    /// <summary>Npgsql connection string, set when the shop runs on PostgreSQL.</summary>
    public string? PostgresConnectionString { get; init; }

    /// <summary>StackExchange.Redis options, set when the shop runs on Redis.</summary>
    public ConfigurationOptions? Redis { get; init; }

    public static Settings Load(IConfiguration configuration) => Load(key => configuration[key]);

    public static Settings Load(Func<string, string?> get)
    {
        var problems = new List<string>();
        string Required(string name)
        {
            var value = get(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"{name} is not set");
                return "";
            }

            return value;
        }

        var shopName = Required("SHOP_NAME");
        var wallet = Required("WALLET_ADDRESS");
        var adminPassword = Required("ADMIN_PASSWORD");
        var databaseUrl = get("DATABASE_URL");
        var redisUrl = get("REDIS_URL");
        var hasDatabase = !string.IsNullOrWhiteSpace(databaseUrl);
        var hasRedis = !string.IsNullOrWhiteSpace(redisUrl);

        string? postgres = null;
        ConfigurationOptions? redis = null;
        if (hasDatabase == hasRedis)
        {
            problems.Add("exactly one of DATABASE_URL or REDIS_URL must be set");
        }
        else if (hasDatabase)
        {
            postgres = ToNpgsql(databaseUrl!);
            if (postgres is null)
            {
                problems.Add("DATABASE_URL is not a valid postgresql:// URI with host and database");
            }
        }
        else
        {
            var password = Required("REDIS_PASSWORD");
            redis = ToRedis(redisUrl!, password);
            if (redis is null)
            {
                problems.Add("REDIS_URL is not a valid redis:// URI with host");
            }
        }

        if (problems.Count > 0)
        {
            throw new SettingsException("Invalid configuration: " + string.Join("; ", problems) + ".");
        }

        return new Settings
        {
            ShopName = shopName.Trim(),
            WalletAddress = wallet.Trim(),
            AdminPassword = adminPassword,
            PostgresConnectionString = postgres,
            Redis = redis,
        };
    }

    /// <summary>Converts a CNPG <c>uri</c> (<c>postgresql://user:pw@host:5432/db</c>) for Npgsql.</summary>
    internal static string? ToNpgsql(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)
            || u.Scheme is not ("postgresql" or "postgres")
            || string.IsNullOrEmpty(u.DnsSafeHost))
        {
            return null;
        }

        var database = Uri.UnescapeDataString(u.AbsolutePath.Trim('/'));
        if (database.Length == 0)
        {
            return null;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = u.DnsSafeHost,
            Port = u.IsDefaultPort || u.Port < 0 ? 5432 : u.Port,
            Database = database,
            ApplicationName = "shop",
            // CNPG doesn't use GSSAPI, and the chiseled image has no libgssapi_krb5.
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        var userInfo = u.UserInfo.Split(':', 2);
        if (userInfo[0].Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length == 2)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                if (!Enum.TryParse<SslMode>(kv[1].Replace("-", "", StringComparison.Ordinal), true, out var mode))
                {
                    return null;
                }

                builder.SslMode = mode;
            }
        }

        return builder.ConnectionString;
    }

    /// <summary>Builds Redis options from <c>REDIS_URL</c> (no credentials) and <c>REDIS_PASSWORD</c>.</summary>
    internal static ConfigurationOptions? ToRedis(string url, string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)
            || u.Scheme is not ("redis" or "rediss")
            || string.IsNullOrEmpty(u.DnsSafeHost))
        {
            return null;
        }

        var options = new ConfigurationOptions
        {
            Password = password,
            AbortOnConnectFail = false,
            Ssl = u.Scheme == "rediss",
            ClientName = "shop",
            ConnectTimeout = 5000,
        };
        options.EndPoints.Add(u.DnsSafeHost, u.Port < 0 ? 6379 : u.Port);
        return options;
    }
}

/// <summary>Missing or invalid configuration. The message names variables only.</summary>
public sealed class SettingsException(string message) : Exception(message);
