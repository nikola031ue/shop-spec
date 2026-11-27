using System.Globalization;
using System.Numerics;
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
/// Spec 005 adds: the order JSON also carries <c>payingAccount</c> and <c>deadline</c> and is never
/// rewritten; its mutable payment state lives in hash <c>shop:order-state:{ref}</c>; waiting orders
/// are indexed by deadline (<c>shop:waiting</c>), account and address; transfers are
/// <c>shop:payment:{tx}:{log}</c> with sorted sets <c>shop:payments</c>/<c>shop:payments-unmatched</c>;
/// chain progress is hash <c>shop:chain-progress</c> (address → "block|ms").
/// </summary>
public sealed class RedisShopStore(ConfigurationOptions options) : IShopStore, IAsyncDisposable
{
    private const int SchemaVersion = 2;

    /// <summary>Deadline given to orders placed before 0.3.0 (data-model.md "Schema scripts").</summary>
    private static readonly TimeSpan LegacyPaymentWindow = TimeSpan.FromMinutes(30);
    private static readonly RedisKey Schema = "shop:schema";
    private static readonly RedisKey InitLock = "shop:lock:init";
    private static readonly RedisKey Items = "shop:items";
    private static readonly RedisKey ItemNames = "shop:item-names";
    private static readonly RedisKey Orders = "shop:orders";
    private static readonly RedisKey Admin = "shop:admin";
    private static readonly RedisKey ShopWideLock = "shop:admin:lock:" + LockoutKeys.ShopWide;
    private static readonly RedisKey DpKeys = "shop:dpkeys";
    private static readonly RedisKey Waiting = "shop:waiting";
    private static readonly RedisKey Addresses = "shop:addresses";
    private static readonly RedisKey Progress = "shop:chain-progress";
    private static readonly RedisKey Payments = "shop:payments";
    private static readonly RedisKey UnmatchedPayments = "shop:payments-unmatched";
    private static readonly TimeSpan InitLockTtl = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string CreateItemLua = Script("create_item");
    private static readonly string UpdateItemLua = Script("update_item");
    private static readonly string DeleteItemLua = Script("delete_item");
    private static readonly string PlaceOrderLua = Script("place_order");
    private static readonly string FailedSignInLua = Script("failed_signin");
    private static readonly string ReleaseLockLua = Script("release_lock");
    private static readonly string BumpSessionLua = Script("bump_session");
    private static readonly string RecordTransferLua = Script("record_transfer");
    private static readonly string ExpireOrderLua = Script("expire_order");
    private static readonly string AdvanceProgressLua = Script("advance_progress");

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
            if (version < 2)
            {
                await MigrateToPaymentsAsync(db);
            }

            if (version < SchemaVersion)
            {
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
            order.PayingAccount,
            order.Deadline.ToUnixTimeMilliseconds(),
        };
        foreach (var line in order.Lines)
        {
            args.Add(Id(line.ItemId));
            args.Add(line.Quantity);
            args.Add(line.ExpectedUnitPriceCents);
        }

        var json = (string)(await (await DbAsync()).ScriptEvaluateAsync(PlaceOrderLua,
            [
                Items, OrderKey(order.Reference), Orders, WaitingAccountKey(order.PayingAccount), Waiting,
                WaitingAddressKey(order.WalletAddress), AccountLastKey(order.PayingAccount, order.WalletAddress), Addresses,
            ],
            [.. args]))!;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("waiting", out var waiting))
        {
            return PlaceOrderResult.Waiting(waiting.GetString()!);
        }

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

        return PlaceOrderResult.Placed(ToOrder(Parse<StoredOrder>(json), []));
    }

    public async Task<Page<Order>> ListOrdersAsync(int page, int pageSize, CancellationToken ct)
    {
        var db = await DbAsync();
        var total = await db.SortedSetLengthAsync(Orders);
        var start = (long)(page - 1) * pageSize;
        var references = await db.SortedSetRangeByRankAsync(Orders, start, start + pageSize - 1, SortOrder.Descending);
        var orders = await LoadOrdersAsync(db, references.Select(r => (string)r!).ToList());
        return new Page<Order>(orders, page, pageSize, total);
    }

    public async Task<Order?> GetOrderAsync(string reference, CancellationToken ct) =>
        (await LoadOrdersAsync(await DbAsync(), [reference])).SingleOrDefault();

    public async Task<TransferOutcome> RecordTransferAsync(ChainTransfer transfer, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var t = transfer;
        var result = (string?)await (await DbAsync()).ScriptEvaluateAsync(RecordTransferLua, [],
        [
            t.Tx, t.LogIndex, t.Block, t.BlockTime.ToUnixTimeMilliseconds(), t.From, t.To, BaseUnits.Format(t.Value),
            receivedAt.ToUnixTimeMilliseconds(),
        ]);
        return result switch
        {
            "paid" => TransferOutcome.Paid,
            "unmatched" => TransferOutcome.Unmatched,
            _ => TransferOutcome.AlreadyRecorded,
        };
    }

    public async Task AdvanceChainProgressAsync(ChainProgress progress, CancellationToken ct) =>
        await (await DbAsync()).ScriptEvaluateAsync(AdvanceProgressLua, [Progress, Addresses],
            [progress.Address, progress.SeenThroughBlock, progress.SeenThroughTime.ToUnixTimeMilliseconds()]);

    public async Task<ChainProgress?> GetChainProgressAsync(string address, CancellationToken ct)
    {
        var value = (string?)await (await DbAsync()).HashGetAsync(Progress, address);
        if (value?.Split('|') is not [var block, var ms])
        {
            return null;
        }

        return new ChainProgress(address, long.Parse(block, CultureInfo.InvariantCulture),
            DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(ms, CultureInfo.InvariantCulture)));
    }

    public async Task<bool> IsKnownAddressAsync(string address, CancellationToken ct) =>
        await (await DbAsync()).SetContainsAsync(Addresses, address);

    public async Task<int> ExpireDueOrdersAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        (int)await (await DbAsync()).ScriptEvaluateAsync(ExpireOrderLua, [Waiting, Items, Progress],
            [now.ToUnixTimeMilliseconds(), limit]);

    public async Task<long> CountWaitingAsync(string address, CancellationToken ct) =>
        await (await DbAsync()).SetLengthAsync(WaitingAddressKey(address));

    public async Task<Page<Payment>> ListPaymentsAsync(bool unmatchedOnly, int page, int pageSize, CancellationToken ct)
    {
        var db = await DbAsync();
        var set = unmatchedOnly ? UnmatchedPayments : Payments;
        var total = await db.SortedSetLengthAsync(set);
        var start = (long)(page - 1) * pageSize;
        var members = await db.SortedSetRangeByRankAsync(set, start, start + pageSize - 1, SortOrder.Descending);
        var values = members.Length == 0
            ? []
            : await db.StringGetAsync(members.Select(m => (RedisKey)("shop:payment:" + m)).ToArray());
        var payments = values.Where(v => !v.IsNull).Select(v => ToPayment(Parse<StoredPayment>(v))).ToList();
        return new Page<Payment>(payments, page, pageSize, total);
    }

    /// <summary>
    /// Version 2 (spec 005): index orders placed before 0.3.0 as waiting, with the legacy 30-minute
    /// deadline. Their JSON stays as written; they have no paying account, so they can only expire.
    /// </summary>
    private static async Task MigrateToPaymentsAsync(IDatabase db)
    {
        foreach (var reference in await db.SortedSetRangeByRankAsync(Orders))
        {
            var value = await db.StringGetAsync(OrderKey((string)reference!));
            if (value.IsNull)
            {
                continue;
            }

            var stored = Parse<StoredOrder>(value);
            var state = (string?)await db.HashGetAsync(OrderStateKey(stored.Reference), "status");
            if ((state ?? stored.Status) != OrderStatus.PendingPayment)
            {
                continue;
            }

            var deadline = stored.Deadline ?? stored.CreatedAt + (long)LegacyPaymentWindow.TotalMilliseconds;
            await db.SortedSetAddAsync(Waiting, stored.Reference, deadline);
            await db.SetAddAsync(WaitingAddressKey(stored.WalletAddress), stored.Reference);
            await db.SetAddAsync(Addresses, stored.WalletAddress);
        }
    }

    private static async Task<List<Order>> LoadOrdersAsync(IDatabase db, List<string> references)
    {
        if (references.Count == 0)
        {
            return [];
        }

        var values = await db.StringGetAsync(references.Select(OrderKey).ToArray());
        var states = await Task.WhenAll(references.Select(r => db.HashGetAllAsync(OrderStateKey(r))));
        var orders = new List<Order>(references.Count);
        for (var i = 0; i < references.Count; i++)
        {
            if (!values[i].IsNull)
            {
                orders.Add(ToOrder(Parse<StoredOrder>(values[i]), states[i]));
            }
        }

        return orders;
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
            long.TryParse(fields.GetValueOrDefault("sessionVersion"), CultureInfo.InvariantCulture, out var v) ? v : 1);
    }

    public async Task<LockoutState> GetLockoutAsync(string account, string address, CancellationToken ct)
    {
        LockoutKeys.Validate(account, address);
        var db = await DbAsync();
        var scope = db.HashGetAllAsync(LockKey(account, address));
        var shopWide = db.HashGetAllAsync(ShopWideLock);
        return new LockoutState(ToScope(await scope), ToScope(await shopWide));
    }

    public async Task<LockoutState> RecordFailedSignInAsync(
        LockoutRules rules, string account, string address, DateTimeOffset now, CancellationToken ct)
    {
        LockoutKeys.Validate(account, address);
        await (await DbAsync()).ScriptEvaluateAsync(FailedSignInLua, [LockKey(account, address), ShopWideLock],
        [
            now.ToUnixTimeMilliseconds(),
            .. PolicyArgs(rules.PerAccountAddress),
            .. PolicyArgs(rules.ShopWide),
        ]);
        return await GetLockoutAsync(account, address, ct);
    }

    public async Task ResetFailedSignInsAsync(string account, string address, CancellationToken ct)
    {
        LockoutKeys.Validate(account, address);
        await (await DbAsync()).KeyDeleteAsync(LockKey(account, address));
    }

    private static RedisKey LockKey(string account, string address) => $"shop:admin:lock:{account}:{address}";

    private static RedisValue[] PolicyArgs(LockoutPolicy p) =>
    [
        (long)p.Window.TotalMilliseconds, p.MaxFailures, (long)p.LockFor.TotalMilliseconds,
        (long)p.Retention.TotalMilliseconds,
    ];

    private static LockoutScope ToScope(HashEntry[] entries)
    {
        var fields = entries.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
        return new LockoutScope(
            int.TryParse(fields.GetValueOrDefault("failedCount"), CultureInfo.InvariantCulture, out var n) ? n : 0,
            Time(fields.GetValueOrDefault("failedWindowStart")),
            Time(fields.GetValueOrDefault("lockedUntil")));
    }

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

    private static RedisKey OrderStateKey(string reference) => "shop:order-state:" + reference;

    private static RedisKey WaitingAccountKey(string account) => "shop:waiting-account:" + account;

    private static RedisKey WaitingAddressKey(string address) => "shop:waiting-address:" + address;

    private static RedisKey AccountLastKey(string account, string address) => $"shop:account-last:{account}:{address}";

    private static string Id(Guid id) => id.ToString("D");

    private static T Parse<T>(RedisValue value) => JsonSerializer.Deserialize<T>((string)value!, Json)!;

    private static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)!;

    private static string Ms(DateTimeOffset? t) =>
        t is { } v ? v.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) : "";

    private static DateTimeOffset? Time(string? ms) =>
        long.TryParse(ms, CultureInfo.InvariantCulture, out var v) ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null;

    private static Item ToItem(StoredItem s) => new(Guid.Parse(s.Id), s.Name, s.Stock, s.PriceCents,
        DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAt), DateTimeOffset.FromUnixTimeMilliseconds(s.UpdatedAt));

    private static Order ToOrder(StoredOrder s, HashEntry[] state)
    {
        var fields = state.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
        var status = fields.GetValueOrDefault("status") ?? s.Status;
        OrderPayment? payment = null;
        if (status == OrderStatus.Paid)
        {
            payment = new OrderPayment(
                fields["paidTx"]!,
                int.Parse(fields["paidLogIndex"]!, CultureInfo.InvariantCulture),
                long.Parse(fields["paidBlock"]!, CultureInfo.InvariantCulture),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(fields["paidAt"]!, CultureInfo.InvariantCulture)));
        }

        var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAt);
        var deadline = s.Deadline is { } d ? DateTimeOffset.FromUnixTimeMilliseconds(d) : createdAt + LegacyPaymentWindow;
        return new Order(s.Reference, createdAt, status, s.WalletAddress, s.TotalCents, s.Lines, s.PayingAccount, deadline,
            payment);
    }

    private static Payment ToPayment(StoredPayment p) => new(
        p.Tx, p.LogIndex, p.Block, DateTimeOffset.FromUnixTimeMilliseconds(p.BlockTime), p.FromAccount, p.ToAddress,
        BigInteger.Parse(p.Value, CultureInfo.InvariantCulture), p.OrderReference, p.UnmatchedReason,
        DateTimeOffset.FromUnixTimeMilliseconds(p.ReceivedAt));

    private sealed record StoredItem(
        string Id, string Name, string NameKey, int Stock, long PriceCents, long CreatedAt, long UpdatedAt);

    private sealed record StoredOrder(
        string Reference, long CreatedAt, string Status, string WalletAddress, long TotalCents, List<OrderLine> Lines,
        string? PayingAccount = null, long? Deadline = null);

    private sealed record StoredPayment(
        string Tx, int LogIndex, long Block, long BlockTime, string FromAccount, string ToAddress, string Value,
        string? OrderReference, string? UnmatchedReason, long ReceivedAt);
}
