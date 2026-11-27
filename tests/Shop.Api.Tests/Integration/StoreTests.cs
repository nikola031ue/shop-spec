using System.Reflection;
using Npgsql;
using Shop.Api.Domain;
using Shop.Api.Storage;
using Shop.Api.Storage.Postgres;
using Shop.Api.Storage.Redis;
using StackExchange.Redis;

namespace Shop.Api.Tests.Integration;

/// <summary>The store contract on PostgreSQL (Testcontainers, same major as the CNPG default).</summary>
public sealed class PostgresStoreTests(PostgresContainer db) : ShopStoreContract, IClassFixture<PostgresContainer>
{
    protected override IShopStore CreateStore() => new PostgresShopStore(db.ConnectionString);

    protected override async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Spec 005 upgrade: a database as 0.2.0 left it (scripts 001–003, an order in the old columns)
    /// gets 004; the old order waits with the 30-minute deadline, can't match, and expires normally.
    /// </summary>
    [Fact]
    public async Task UpgradeFrom020KeepsOrdersAndGivesThemADeadline()
    {
        await ResetAsync();
        var created = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        await using (var conn = new NpgsqlConnection(db.ConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            var sql = "CREATE TABLE schema_version (version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());";
            var assembly = typeof(PostgresShopStore).Assembly;
            foreach (var n in new[] { "001_initial", "002_session_version", "003_lockout_per_account_address" })
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream($"pg/{n}.sql")!);
                sql += reader.ReadToEnd() + Environment.NewLine + $"INSERT INTO schema_version (version) VALUES ({n[..3]});" + Environment.NewLine;
            }

            sql += """
                INSERT INTO items (id, name, name_key, stock, price_cents, created_at, updated_at)
                VALUES ('0193f000-0000-7000-8000-000000000001', 'Shirt', 'shirt', 3, 1500, now(), now());
                INSERT INTO orders (reference, created_at, status, wallet_address, total_cents, lines)
                VALUES ('OLD0000001', '2026-11-20T09:00:00Z', 'PendingPayment', '0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed', 3000,
                        '[{"itemId":"0193f000-0000-7000-8000-000000000001","name":"Shirt","unitPriceCents":1500,"quantity":2}]');
                """;
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await AssertUpgradedAsync(CreateStore(), created);
    }

    internal static async Task AssertUpgradedAsync(IShopStore store, DateTimeOffset created)
    {
        var ct = TestContext.Current.CancellationToken;
        const string wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
        await store.InitializeAsync(existing => existing ?? new AdminRecord("hash", true, null), ct);

        var order = (await store.GetOrderAsync("OLD0000001", ct))!;
        Assert.Equal((OrderStatus.PendingPayment, (string?)null, created.AddMinutes(30)), (order.Status, order.PayingAccount, order.Deadline));
        Assert.Equal(1, await store.CountWaitingAsync(wallet, ct));
        Assert.True(await store.IsKnownAddressAsync(wallet, ct));

        await store.AdvanceChainProgressAsync(new ChainProgress(wallet, 10, created.AddHours(1)), ct);
        Assert.Equal(1, await store.ExpireDueOrdersAsync(created.AddHours(2), 100, ct));
        Assert.Equal(OrderStatus.Expired, (await store.GetOrderAsync("OLD0000001", ct))!.Status);
        Assert.Equal(5, (await store.GetItemAsync(Guid.Parse("0193f000-0000-7000-8000-000000000001"), ct))!.Stock);
    }
}

/// <summary>The store contract on Redis 7.2 with authentication (Testcontainers).</summary>
public sealed class RedisStoreTests(RedisContainer redis) : ShopStoreContract, IClassFixture<RedisContainer>
{
    protected override IShopStore CreateStore() => new RedisShopStore(redis.Options());

    protected override async Task ResetAsync()
    {
        await using var admin = await ConnectionMultiplexer.ConnectAsync(redis.Options(allowAdmin: true));
        await admin.GetServers().Single().FlushAllDatabasesAsync();
    }

    /// <summary>Spec 005 upgrade: schema 1 with an old order becomes schema 2 with the order indexed as waiting.</summary>
    [Fact]
    public async Task UpgradeFrom020KeepsOrdersAndGivesThemADeadline()
    {
        await ResetAsync();
        var created = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var ms = created.ToUnixTimeMilliseconds();
        await using (var conn = await ConnectionMultiplexer.ConnectAsync(redis.Options()))
        {
            var dbx = conn.GetDatabase();
            await dbx.StringSetAsync("shop:schema", 1);
            await dbx.HashSetAsync("shop:items", "0193f000-0000-7000-8000-000000000001",
                $$"""{"id":"0193f000-0000-7000-8000-000000000001","name":"Shirt","nameKey":"shirt","stock":3,"priceCents":1500,"createdAt":{{ms}},"updatedAt":{{ms}}}""");
            await dbx.StringSetAsync("shop:order:OLD0000001",
                $$"""{"reference":"OLD0000001","createdAt":{{ms}},"status":"PendingPayment","walletAddress":"0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed","totalCents":3000,"lines":[{"itemId":"0193f000-0000-7000-8000-000000000001","name":"Shirt","unitPriceCents":1500,"quantity":2}]}""");
            await dbx.SortedSetAddAsync("shop:orders", "OLD0000001", ms);
        }

        await PostgresStoreTests.AssertUpgradedAsync(CreateStore(), created);
        await using var check = await ConnectionMultiplexer.ConnectAsync(redis.Options());
        Assert.Equal(2, (int)await check.GetDatabase().StringGetAsync("shop:schema"));
    }

    [Fact]
    public async Task UnauthenticatedClientsAreRefused()
    {
        var options = redis.Options();
        options.Password = null;
        await using var anonymous = await ConnectionMultiplexer.ConnectAsync(options);

        await Assert.ThrowsAnyAsync<RedisException>(() => anonymous.GetDatabase().PingAsync());
    }
}
