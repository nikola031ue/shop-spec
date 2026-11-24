using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace Shop.Api.Tests.Integration;

/// <summary>
/// PostgreSQL in the same major version as the CNPG default image on the cluster
/// (ghcr.io/cloudnative-pg/postgresql:18.7, CNPG 1.30.2).
/// </summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    public const string Image = "postgres:18.7";

    private readonly PostgreSqlContainer _container;

    public PostgresContainer()
    {
        _container = new PostgreSqlBuilder(Image).WithPassword(Password).Build();
    }

    /// <summary>A unique password, so log-leak checks can search for it.</summary>
    public string Password { get; } = "pg-" + Guid.NewGuid().ToString("N");

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>The CNPG-style URI the operator injects as <c>DATABASE_URL</c>.</summary>
    public string DatabaseUrl(string database = "postgres", string? password = null) =>
        $"postgresql://postgres:{password ?? Password}@{_container.Hostname}:{_container.GetMappedPublicPort(5432)}/{database}";

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database and returns its URI.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return DatabaseUrl(name);
    }
}

/// <summary>Redis 7.2 (as run by the OT redis-operator) with <c>--requirepass</c>, like light shops.</summary>
public sealed class RedisContainer : IAsyncLifetime
{
    public const string Image = "redis:7.2.16";

    public string Password { get; } = Guid.NewGuid().ToString("N");

    private readonly IContainer _container;

    public RedisContainer()
    {
        _container = new ContainerBuilder(Image)
            .WithCommand("redis-server", "--requirepass", Password)
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
    }

    /// <summary>The credential-free URL the operator injects as <c>REDIS_URL</c>.</summary>
    public string Url => $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";

    public ConfigurationOptions Options(bool allowAdmin = false)
    {
        var options = Settings.ToRedis(Url, Password)!;
        options.AllowAdmin = allowAdmin;
        return options;
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
