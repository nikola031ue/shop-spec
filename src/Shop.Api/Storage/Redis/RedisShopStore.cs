using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Shop.Api.Domain;
using StackExchange.Redis;
using Order = Shop.Api.Domain.Order;
using SortOrder = StackExchange.Redis.Order;

namespace Shop.Api.Storage.Redis;

/// <summary>
/// Redis store (light shops): hashes and a sorted set, with every multi-key change in a Lua script
/// (data-model.md "Redis layout", research R9, R12). Times are stored as Unix milliseconds.
/// </summary>
public sealed class RedisShopStore(ConfigurationOptions options) : IShopStore, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private static readonly RedisKey Schema = "shop:schema";
    private static readonly RedisKey InitLock = "shop:lock:init";
    private static readonly RedisKey Items = "shop:items";
    private static readonly RedisKey ItemNames = "shop:item-names";
    private static readonly RedisKey Orders = "shop:orders";
    private static readonly RedisKey Admin = "shop:admin";
    private static readonly RedisKey DpKeys = "shop:dpkeys";
    private static readonly TimeSpan InitLockTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string CreateItemLua = Script("create_item");
    private static readonly string UpdateItemLua = Script("update_item");
    private static readonly string DeleteItemLua = Script("delete_item");
    private static readonly string PlaceOrderLua = Script("place_order");
    private static readonly string FailedSignInLua = Script("failed_signin");
    private static readonly string ReleaseLockLua = Script("release_lock");
    private static readonly string BumpSessionLua = Script("bump_session");

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private ConnectionMultiplexer? _connection;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _connectLock.Dispose();
    }

    public async Task InitializeAsync(Func<AdminRecord?, AdminRecord?> bootstrapAdmin, CancellationToken ct)
    {
        var db = await DbAsync();
        var token = Guid.NewGuid().ToString("N");
        while (!await db.StringSetAsync(InitLock, token, InitLockTtl, When.NotExists))
        {
            await Task.Delay(200, ct);
        }

        try
        {
            var version = (int?)await db.StringGetAsync(Schema) ?? 0;
            if (version < SchemaVersion)
            {
                // Version 1 needs no data changes; later versions migrate here.
                await db.StringSetAsync(Schema, SchemaVersion);
            }

            var next = bootstrapAdmin(await GetAdminAsync(ct));
            if (next is not null)
            {
                await db.HashSetAsync(Admin,
                [
                    new("passwordHash", next.PasswordHash),
                    new("initial", next.Initial ? "1" : "0"),
                    new("changedAt", Ms(next.ChangedAt)),
                    new("failedCount", next.FailedCount),
                    new("failedWindowStart", Ms(next.FailedWindowStart)),
                    new("lockedUntil", Ms(next.LockedUntil)),
                    new("sessionVersion", next.SessionVersion),
                ]);
            }
        }
        finally
        {
            await db.ScriptEvaluateAsync(ReleaseLockLua, [InitLock], [token]);
        }
    }

    public async Task PingAsync(CancellationToken ct) => await (await DbAsync()).PingAsync();

    public async Task<Page<Item>> ListItemsAsync(string? query, int page, int pageSize, CancellationToken ct)
    {
        // ≤ 1,000 items per shop: one HGETALL, filtered and sorted here (data-model.md).
        var all = (await (await DbAsync()).HashValuesAsync(Items))
            .Select(v => ToItem(Parse<StoredItem>(v)))
            .Where(i => string.IsNullOrEmpty(query) || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => ItemRules.NameKey(i.Name), StringComparer.Ordinal)
            .ThenBy(i => i.Id.ToString(), StringComparer.Ordinal)
            .ToList();
        return new Page<Item>(all.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, all.Count);
    }

    public async Task<Item?> GetItemAsync(Guid id, CancellationToken ct)
    {
        var value = await (await DbAsync()).HashGetAsync(Items, Id(id));
        return value.IsNull ? null : ToItem(Parse<StoredItem>(value));
    }

    public async Task<ItemWrite> CreateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct)
    {
        var ms = now.ToUnixTimeMilliseconds();
        var stored = new StoredItem(Id(id), draft.Name, draft.NameKey, draft.Stock, draft.PriceCents, ms, ms);
        var result = (string?)await (await DbAsync()).ScriptEvaluateAsync(CreateItemLua,
            [Items, ItemNames], [stored.Id, stored.NameKey, JsonSerializer.Serialize(stored, Json)]);
        return result == "ok"
            ? new ItemWrite(ItemWriteStatus.Ok, ToItem(stored))
            : new ItemWrite(ItemWriteStatus.NameExists);
    }

    public async Task<ItemWrite> UpdateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct)
    {
        var result = (RedisResult[])(await (await DbAsync()).ScriptEvaluateAsync(UpdateItemLua,
            [Items, ItemNames],
            [Id(id), draft.Name, draft.NameKey, draft.Stock, draft.PriceCents, now.ToUnixTimeMilliseconds()]))!;
        return (string?)result[0] switch
        {
            "ok" => new ItemWrite(ItemWriteStatus.Ok, ToItem(Parse<StoredItem>((string)result[1]!))),
            "notfound" => new ItemWrite(ItemWriteStatus.NotFound),
            _ => new ItemWrite(ItemWriteStatus.NameExists),
        };
    }

    public async Task<bool> DeleteItemAsync(Guid id, CancellationToken ct) =>
        (int)await (await DbAsync()).ScriptEvaluateAsync(DeleteItemLua, [Items, ItemNames], [Id(id)]) == 1;

    public async Task<PlaceOrderResult> PlaceOrderAsync(NewOrder order, CancellationToken ct)
    {
        var args = new List<RedisValue>
        {
            order.Reference,
            order.CreatedAt.ToUnixTimeMilliseconds(),
            order.WalletAddress,
            OrderStatus.PendingPayment,
        };
        foreach (var line in order.Lines)
        {
            args.Add(Id(line.ItemId));
            args.Add(line.Quantity);
            args.Add(line.ExpectedUnitPriceCents);
        }

        var json = (string)(await (await DbAsync()).ScriptEvaluateAsync(PlaceOrderLua,
            [Items, OrderKey(order.Reference), Orders], [.. args]))!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("taken", out _))
        {
            return PlaceOrderResult.Taken;
        }

        if (root.TryGetProperty("problems", out var problems))
        {
            return PlaceOrderResult.Rejected(problems.EnumerateArray().Select(p => new OrderProblem(
                Guid.Parse(p.GetProperty("itemId").GetString()!),
                p.GetProperty("kind").GetString()!,
                p.TryGetProperty("currentPriceCents", out var price) ? price.GetInt64() : null,
                p.TryGetProperty("currentStock", out var stock) ? stock.GetInt32() : null)).ToList());
        }

        return PlaceOrderResult.Placed(ToOrder(Parse<StoredOrder>(json)));
    }

    public async Task<Page<Order>> ListOrdersAsync(int page, int pageSize, CancellationToken ct)
    {
        var db = await DbAsync();
        var total = await db.SortedSetLengthAsync(Orders);
        var start = (long)(page - 1) * pageSize;
        var references = await db.SortedSetRangeByRankAsync(Orders, start, start + pageSize - 1, SortOrder.Descending);
        var values = references.Length == 0
            ? []
            : await db.StringGetAsync(references.Select(r => OrderKey((string)r!)).ToArray());
        var orders = values.Where(v => !v.IsNull).Select(v => ToOrder(Parse<StoredOrder>(v))).ToList();
        return new Page<Order>(orders, page, pageSize, total);
    }

    public async Task<AdminRecord?> GetAdminAsync(CancellationToken ct)
    {
        var fields = (await (await DbAsync()).HashGetAllAsync(Admin))
            .ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
        if (!fields.TryGetValue("passwordHash", out var hash) || string.IsNullOrEmpty(hash))
        {
            return null;
        }

        return new AdminRecord(
            hash,
            fields.GetValueOrDefault("initial") == "1",
            Time(fields.GetValueOrDefault("changedAt")),
            int.TryParse(fields.GetValueOrDefault("failedCount"), CultureInfo.InvariantCulture, out var n) ? n : 0,
            Time(fields.GetValueOrDefault("failedWindowStart")),
            Time(fields.GetValueOrDefault("lockedUntil")),
            long.TryParse(fields.GetValueOrDefault("sessionVersion"), CultureInfo.InvariantCulture, out var v) ? v : 1);
    }

    public async Task<AdminRecord> RecordFailedSignInAsync(LockoutPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        await (await DbAsync()).ScriptEvaluateAsync(FailedSignInLua, [Admin],
        [
            now.ToUnixTimeMilliseconds(),
            (long)policy.Window.TotalMilliseconds,
            policy.MaxFailures,
            (long)policy.LockFor.TotalMilliseconds,
        ]);
        return await GetAdminAsync(ct) ?? throw new InvalidOperationException("The admin record is missing.");
    }

    public async Task ResetFailedSignInsAsync(CancellationToken ct) =>
        await (await DbAsync()).HashSetAsync(Admin,
            [new("failedCount", 0), new("failedWindowStart", ""), new("lockedUntil", "")]);

    public async Task<long> SetAdminPasswordAsync(string passwordHash, DateTimeOffset changedAt, CancellationToken ct) =>
        (long)await (await DbAsync()).ScriptEvaluateAsync(BumpSessionLua, [Admin], [passwordHash, Ms(changedAt)]);

    public async Task<long> BumpSessionVersionAsync(CancellationToken ct) =>
        (long)await (await DbAsync()).ScriptEvaluateAsync(BumpSessionLua, [Admin]);

    public async Task<IReadOnlyList<DataProtectionKey>> GetDataProtectionKeysAsync(CancellationToken ct) =>
        (await (await DbAsync()).HashGetAllAsync(DpKeys))
            .Select(e => new DataProtectionKey(e.Name!, e.Value!))
            .OrderBy(k => k.Name, StringComparer.Ordinal)
            .ToList();

    public async Task StoreDataProtectionKeyAsync(string name, string xml, CancellationToken ct) =>
        await (await DbAsync()).HashSetAsync(DpKeys, name, xml);

    private async Task<IDatabase> DbAsync()
    {
        if (_connection is null)
        {
            await _connectLock.WaitAsync();
            try
            {
                // abortConnect=false: returns while Redis is down and reconnects in the background.
                _connection ??= await ConnectionMultiplexer.ConnectAsync(options);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        return _connection.GetDatabase();
    }

    private static string Script(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream($"redis/{name}.lua")
            ?? throw new InvalidOperationException($"Missing embedded script {name}.lua.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static RedisKey OrderKey(string reference) => "shop:order:" + reference;

    private static string Id(Guid id) => id.ToString("D");

    private static T Parse<T>(RedisValue value) => JsonSerializer.Deserialize<T>((string)value!, Json)!;

    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;

    private static string Ms(DateTimeOffset? t) =>
        t is { } v ? v.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : "";

    private static DateTimeOffset? Time(string? ms) =>
        long.TryParse(ms, CultureInfo.InvariantCulture, out var v) ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    private static Item ToItem(StoredItem s) => new(Guid.Parse(s.Id), s.Name, s.Stock, s.PriceCents,
        DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAt), DateTimeOffset.FromUnixTimeMilliseconds(s.UpdatedAt));

    private static Order ToOrder(StoredOrder s) => new(s.Reference, DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAt),
        s.Status, s.WalletAddress, s.TotalCents, s.Lines);

    private sealed record StoredItem(
        string Id, string Name, string NameKey, int Stock, long PriceCents, long CreatedAt, long UpdatedAt);

    private sealed record StoredOrder(
        string Reference, long CreatedAt, string Status, string WalletAddress, long TotalCents, List<OrderLine> Lines);
}
