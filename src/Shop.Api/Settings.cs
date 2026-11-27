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

    /// <summary>
    /// Optional <c>TRUSTED_PROXY_HOST</c>: DNS name whose addresses are the only trusted proxies
    /// (spec 004 FR-022). Null means forwarded headers are ignored.
    /// </summary>
    public string? TrustedProxyHost { get; init; }

    /// <summary>
    /// <c>TOKEN_ADDRESS</c>: the mUSDT contract (EIP-55). Null means payments are unavailable and
    /// checkout is refused (spec 005 contracts/shop-api.md).
    /// </summary>
    public string? TokenAddress { get; init; }

    /// <summary><c>CHAIN_ID</c>, default 11155111 (Sepolia).</summary>
    public long ChainId { get; init; } = DefaultChainId;

    /// <summary><c>EXPLORER_TX_URL</c>: a transaction hash appended gives its explorer page.</summary>
    public string ExplorerTxUrl { get; init; } = DefaultExplorerTxUrl;

    /// <summary><c>ORDER_PAYMENT_MINUTES</c>: the payment window (FR-009), default 30.</summary>
    public int OrderPaymentMinutes { get; init; } = DefaultOrderPaymentMinutes;

    /// <summary>
    /// <c>INTERNAL_TOKEN</c>: bearer token for the operator's internal API. Null means the internal
    /// port is not opened (spec 005 contracts/shop-internal-api.md).
    /// </summary>
    public string? InternalToken { get; init; }

    /// <summary><c>INTERNAL_PORT</c>, default 8081: the internal API's own listener.</summary>
    public int InternalPort { get; init; } = DefaultInternalPort;

    /// <summary>
    /// The public port (<c>ASPNETCORE_HTTP_PORTS</c>, default 8080). Only used to listen explicitly
    /// next to the internal port; otherwise Kestrel's defaults apply.
    /// </summary>
    public int PublicPort { get; init; } = DefaultPublicPort;

    public const long DefaultChainId = 11155111;
    public const string DefaultExplorerTxUrl = "https://sepolia.etherscan.io/tx/";
    public const int DefaultOrderPaymentMinutes = 30;
    public const int DefaultInternalPort = 8081;
    public const int DefaultPublicPort = 8080;

    /// <summary>The shortest internal token accepted; the operator generates 32 random bytes (base64, 44 characters).</summary>
    public const int MinInternalTokenLength = 32;

    public bool PaymentsEnabled => TokenAddress is not null;

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

        var proxyHost = get("TRUSTED_PROXY_HOST")?.Trim();
        if (string.IsNullOrEmpty(proxyHost))
        {
            proxyHost = null;
        }
        else if (Uri.CheckHostName(proxyHost) == UriHostNameType.Unknown)
        {
            problems.Add("TRUSTED_PROXY_HOST is not a DNS name or address");
        }

        string? tokenAddress = null;
        if (Optional("TOKEN_ADDRESS") is { } token)
        {
            tokenAddress = Domain.Eip55.Normalize(token, out _);
            if (tokenAddress is null)
            {
                problems.Add("TOKEN_ADDRESS is not a valid Ethereum address");
            }
        }

        var chainId = Number("CHAIN_ID", DefaultChainId, 1, long.MaxValue);
        var orderMinutes = (int)Number("ORDER_PAYMENT_MINUTES", DefaultOrderPaymentMinutes, 1, 24 * 60);
        var internalPort = (int)Number("INTERNAL_PORT", DefaultInternalPort, 0, 65535);
        var publicPort = (int)Number("HTTP_PORTS", DefaultPublicPort, 0, 65535);

        var explorer = Optional("EXPLORER_TX_URL") ?? DefaultExplorerTxUrl;
        if (!Uri.TryCreate(explorer, UriKind.Absolute, out var explorerUri) || explorerUri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("EXPLORER_TX_URL is not an https URL");
        }

        var internalToken = Optional("INTERNAL_TOKEN");
        if (internalToken is not null && internalToken.Length < MinInternalTokenLength)
        {
            problems.Add($"INTERNAL_TOKEN is shorter than {MinInternalTokenLength} characters");
        }

        if (internalToken is not null && internalPort != 0 && internalPort == publicPort)
        {
            problems.Add("INTERNAL_PORT must differ from the public port");
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
            TrustedProxyHost = proxyHost,
            TokenAddress = tokenAddress,
            ChainId = chainId,
            ExplorerTxUrl = explorer,
            OrderPaymentMinutes = orderMinutes,
            InternalToken = internalToken,
            InternalPort = internalPort,
            PublicPort = publicPort,
        };

        string? Optional(string name) => get(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        long Number(string name, long fallback, long min, long max)
        {
            if (Optional(name) is not { } text)
            {
                return fallback;
            }

            // ASPNETCORE_HTTP_PORTS may list several ports ("8080;8081"); the first is the public one.
            text = text.Split(';', ',')[0].Trim();
            if (!long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
                || n < min || n > max)
            {
                problems.Add($"{name} must be a whole number from {min} to {max}");
                return fallback;
            }

            return n;
        }
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
