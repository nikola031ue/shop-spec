using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Shop.Api.Domain;

namespace Shop.Api.Storage.Postgres;

/// <summary>PostgreSQL (CNPG) store: plain Npgsql and hand-written SQL (research R7, R9, R12).</summary>
public sealed class PostgresShopStore(string connectionString) : IShopStore, IAsyncDisposable
{
    /// <summary>Advisory lock key for schema set-up and admin bootstrap ("SHOP" + 1).</summary>
    private const long InitLockKey = 0x5348_4F50_0001;

    private const string ItemColumns = "id, name, stock, price_cents, created_at, updated_at";
    private const string OrderColumns = "reference, created_at, status, wallet_address, total_cents, lines";
    private const string AdminColumns =
        "password_hash, initial, changed_at, failed_count, failed_window_start, locked_until";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    public async Task InitializeAsync(Func<AdminRecord?, AdminRecord?> bootstrapAdmin, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn, "SELECT pg_advisory_lock(@key)", ct, ("key", InitLockKey));
        try
        {
            await ExecuteAsync(conn, """
                CREATE TABLE IF NOT EXISTS schema_version (
                    version    integer     PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now())
                """, ct);
            var applied = new HashSet<int>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM schema_version", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    applied.Add(reader.GetInt32(0));
                }
            }

            foreach (var (version, sql) in Scripts().Where(s => !applied.Contains(s.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await ExecuteAsync(conn, sql, ct);
                await ExecuteAsync(conn, "INSERT INTO schema_version (version) VALUES (@v)", ct, ("v", version));
                await tx.CommitAsync(ct);
            }

            var next = bootstrapAdmin(await ReadAdminAsync(conn, forUpdate: false, ct));
            if (next is not null)
            {
                await ExecuteAsync(conn, $"""
                    INSERT INTO admin (id, {AdminColumns}) VALUES (1, @hash, @initial, @changed, @count, @start, @locked)
                    ON CONFLICT (id) DO UPDATE SET password_hash = @hash, initial = @initial, changed_at = @changed,
                        failed_count = @count, failed_window_start = @start, locked_until = @locked
                    """, ct, AdminParameters(next));
            }
        }
        finally
        {
            await ExecuteAsync(conn, "SELECT pg_advisory_unlock(@key)", CancellationToken.None, ("key", InitLockKey));
        }
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<Page<Item>> ListItemsAsync(string? query, int page, int pageSize, CancellationToken ct)
    {
        var pattern = string.IsNullOrEmpty(query) ? null : "%" + EscapeLike(query) + "%";
        const string Filter = @"WHERE @pattern::text IS NULL OR name ILIKE @pattern ESCAPE '\'";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var items = new List<Item>();
        await using (var cmd = Command(conn, $"""
            SELECT {ItemColumns} FROM items {Filter}
            ORDER BY name_key COLLATE "C", id LIMIT @limit OFFSET @offset
            """, ("pattern", Text(pattern)), ("limit", pageSize), ("offset", (page - 1) * pageSize)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                items.Add(ReadItem(reader));
            }
        }

        await using var count = Command(conn, $"SELECT count(*) FROM items {Filter}", ("pattern", Text(pattern)));
        var total = (long)(await count.ExecuteScalarAsync(ct))!;
        return new Page<Item>(items, page, pageSize, total);
    }

    public async Task<Item?> GetItemAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = Command(conn, $"SELECT {ItemColumns} FROM items WHERE id = @id", ("id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    public async Task<ItemWrite> CreateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct)
    {
        now = StoreTime.Truncate(now);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = Command(conn, $"""
            INSERT INTO items ({ItemColumns}, name_key) VALUES (@id, @name, @stock, @price, @now, @now, @key)
            ON CONFLICT (name_key) DO NOTHING
            RETURNING {ItemColumns}
            """, ("id", id), ("name", draft.Name), ("stock", draft.Stock), ("price", draft.PriceCents),
            ("now", now), ("key", draft.NameKey));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ItemWrite(ItemWriteStatus.Ok, ReadItem(reader))
            : new ItemWrite(ItemWriteStatus.NameExists);
    }

    public async Task<ItemWrite> UpdateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct)
    {
        now = StoreTime.Truncate(now);
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = Command(conn, $"""
            UPDATE items SET name = @name, name_key = @key, stock = @stock, price_cents = @price, updated_at = @now
            WHERE id = @id
            RETURNING {ItemColumns}
            """, ("id", id), ("name", draft.Name), ("stock", draft.Stock), ("price", draft.PriceCents),
            ("now", now), ("key", draft.NameKey));
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct)
                ? new ItemWrite(ItemWriteStatus.Ok, ReadItem(reader))
                : new ItemWrite(ItemWriteStatus.NotFound);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new ItemWrite(ItemWriteStatus.NameExists);
        }
    }

    public async Task<bool> DeleteItemAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await ExecuteAsync(conn, "DELETE FROM items WHERE id = @id", ct, ("id", id)) == 1;
    }

    public async Task<PlaceOrderResult> PlaceOrderAsync(NewOrder order, CancellationToken ct)
    {
        var createdAt = StoreTime.Truncate(order.CreatedAt);
        var ids = order.Lines.Select(l => l.ItemId).ToArray();
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Lock the rows in id order so concurrent orders never deadlock (research R9).
        var current = new Dictionary<Guid, (string Name, int Stock, long Price)>();
        await using (var cmd = Command(conn,
            "SELECT id, name, stock, price_cents FROM items WHERE id = ANY(@ids) ORDER BY id FOR UPDATE",
            ("ids", ids)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                current[reader.GetGuid(0)] = (reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3));
            }
        }

        var problems = new List<OrderProblem>();
        var lines = new List<OrderLine>();
        foreach (var line in order.Lines)
        {
            if (!current.TryGetValue(line.ItemId, out var item))
            {
                problems.Add(new OrderProblem(line.ItemId, ProblemKind.Removed));
            }
            else if (item.Price != line.UnitPriceCents)
            {
                problems.Add(new OrderProblem(line.ItemId, ProblemKind.Price, item.Price, item.Stock));
            }
            else if (item.Stock < line.Quantity)
            {
                problems.Add(new OrderProblem(line.ItemId, ProblemKind.Stock, CurrentStock: item.Stock));
            }
            else
            {
                lines.Add(new OrderLine(line.ItemId, item.Name, line.UnitPriceCents, line.Quantity));
            }
        }

        if (problems.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return PlaceOrderResult.Rejected(problems);
        }

        await ExecuteAsync(conn, """
            UPDATE items SET stock = items.stock - v.q, updated_at = @now
            FROM unnest(@ids, @qs) AS v(id, q) WHERE items.id = v.id
            """, ct, ("ids", ids), ("qs", order.Lines.Select(l => l.Quantity).ToArray()), ("now", createdAt));

        var placed = new Order(order.Reference, createdAt, OrderStatus.PendingPayment, order.WalletAddress,
            order.TotalCents, lines);
        var inserted = await ExecuteAsync(conn, $"""
            INSERT INTO orders ({OrderColumns}) VALUES (@ref, @at, @status, @wallet, @total, @lines)
            ON CONFLICT (reference) DO NOTHING
            """, ct, ("ref", placed.Reference), ("at", createdAt), ("status", placed.Status),
            ("wallet", placed.WalletAddress), ("total", placed.TotalCents), ("lines", Jsonb(placed.Lines)));
        if (inserted == 0)
        {
            await tx.RollbackAsync(ct);
            return PlaceOrderResult.Taken;
        }

        await tx.CommitAsync(ct);
        return PlaceOrderResult.Placed(placed);
    }

    public async Task<Page<Order>> ListOrdersAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var orders = new List<Order>();
        await using (var cmd = Command(conn, $"""
            SELECT {OrderColumns} FROM orders ORDER BY created_at DESC, reference DESC LIMIT @limit OFFSET @offset
            """, ("limit", pageSize), ("offset", (page - 1) * pageSize)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                orders.Add(new Order(
                    reader.GetString(0),
                    reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    JsonSerializer.Deserialize<List<OrderLine>>(reader.GetString(5), Json)!));
            }
        }

        await using var count = Command(conn, "SELECT count(*) FROM orders");
        return new Page<Order>(orders, page, pageSize, (long)(await count.ExecuteScalarAsync(ct))!);
    }

    public async Task<AdminRecord?> GetAdminAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await ReadAdminAsync(conn, forUpdate: false, ct);
    }

    public async Task<AdminRecord> RecordFailedSignInAsync(LockoutPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var admin = await ReadAdminAsync(conn, forUpdate: true, ct)
            ?? throw new InvalidOperationException("The admin record is missing.");
        var next = policy.ApplyFailure(admin, StoreTime.Truncate(now));
        await ExecuteAsync(conn, """
            UPDATE admin SET failed_count = @count, failed_window_start = @start, locked_until = @locked WHERE id = 1
            """, ct, AdminParameters(next));
        await tx.CommitAsync(ct);
        return next;
    }

    public async Task ResetFailedSignInsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn,
            "UPDATE admin SET failed_count = 0, failed_window_start = NULL, locked_until = NULL WHERE id = 1", ct);
    }

    public async Task SetAdminPasswordAsync(string passwordHash, DateTimeOffset changedAt, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var updated = await ExecuteAsync(conn,
            "UPDATE admin SET password_hash = @hash, initial = false, changed_at = @at WHERE id = 1", ct,
            ("hash", passwordHash), ("at", StoreTime.Truncate(changedAt)));
        if (updated != 1)
        {
            throw new InvalidOperationException("The admin record is missing.");
        }
    }

    public async Task<IReadOnlyList<DataProtectionKey>> GetDataProtectionKeysAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = Command(conn, "SELECT name, xml FROM dp_keys ORDER BY name");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var keys = new List<DataProtectionKey>();
        while (await reader.ReadAsync(ct))
        {
            keys.Add(new DataProtectionKey(reader.GetString(0), reader.GetString(1)));
        }

        return keys;
    }

    public async Task StoreDataProtectionKeyAsync(string name, string xml, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await ExecuteAsync(conn,
            "INSERT INTO dp_keys (name, xml) VALUES (@name, @xml) ON CONFLICT (name) DO UPDATE SET xml = @xml", ct,
            ("name", name), ("xml", xml));
    }

    /// <summary>Embedded <c>Scripts/NNN_*.sql</c>, ordered by number.</summary>
    private static IEnumerable<(int Version, string Sql)> Scripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("pg/", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                var version = int.Parse(n.AsSpan(3, 3), CultureInfo.InvariantCulture);
                return (version, reader.ReadToEnd());
            })
            .OrderBy(s => s.version);
    }

    private static async Task<AdminRecord?> ReadAdminAsync(NpgsqlConnection conn, bool forUpdate, CancellationToken ct)
    {
        var sql = $"SELECT {AdminColumns} FROM admin WHERE id = 1" + (forUpdate ? " FOR UPDATE" : "");
        await using var cmd = Command(conn, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new AdminRecord(
            reader.GetString(0),
            reader.GetBoolean(1),
            NullableTime(reader, 2),
            reader.GetInt32(3),
            NullableTime(reader, 4),
            NullableTime(reader, 5));
    }

    private static (string, object?)[] AdminParameters(AdminRecord a) =>
    [
        ("hash", a.PasswordHash), ("initial", a.Initial), ("changed", Time(a.ChangedAt)),
        ("count", a.FailedCount), ("start", Time(a.FailedWindowStart)), ("locked", Time(a.LockedUntil)),
    ];

    private static Item ReadItem(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetString(1),
        r.GetInt32(2),
        r.GetInt64(3),
        r.GetFieldValue<DateTimeOffset>(4),
        r.GetFieldValue<DateTimeOffset>(5));

    private static DateTimeOffset? NullableTime(DbDataReader r, int i) =>
        r.IsDBNull(i) ? null : r.GetFieldValue<DateTimeOffset>(i);

    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> for <c>LIKE ... ESCAPE '\'</c>.</summary>
    internal static string EscapeLike(string text) => text
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static NpgsqlParameter Text(string? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter Time(DateTimeOffset? value) =>
        new() { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = value is { } v ? StoreTime.Truncate(v) : DBNull.Value };

    private static NpgsqlParameter Jsonb<T>(T value) =>
        new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(value, Json) };

    private static NpgsqlCommand Command(NpgsqlConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
        {
            if (value is NpgsqlParameter p)
            {
                p.ParameterName = name;
                cmd.Parameters.Add(p);
            }
            else
            {
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return cmd;
    }

    private static async Task<int> ExecuteAsync(
        NpgsqlConnection conn, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = Command(conn, sql, parameters);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
