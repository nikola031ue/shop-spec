using Npgsql;
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

    [Fact]
    public async Task UnauthenticatedClientsAreRefused()
    {
        var options = redis.Options();
        options.Password = null;
        await using var anonymous = await ConnectionMultiplexer.ConnectAsync(options);

        await Assert.ThrowsAnyAsync<RedisException>(() => anonymous.GetDatabase().PingAsync());
    }
}
