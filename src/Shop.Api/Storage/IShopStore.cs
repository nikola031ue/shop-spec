using System.Net.Sockets;
using Npgsql;
using Shop.Api.Domain;
using StackExchange.Redis;
using Order = Shop.Api.Domain.Order;

namespace Shop.Api.Storage;

/// <summary>
/// The single storage seam (research R8). <see cref="Postgres.PostgresShopStore"/> and
/// <see cref="Redis.RedisShopStore"/> pass the same contract suite. Times passed in are stored with
/// millisecond precision (<see cref="StoreTime"/>).
/// </summary>
public interface IShopStore
{
    /// <summary>
    /// Creates or upgrades the schema and bootstraps the admin record under a store-wide lock, so
    /// several replicas can start at once (research R12). <paramref name="bootstrapAdmin"/> gets the
    /// current admin record (or null) and returns a record to write, or null to leave it unchanged.
    /// </summary>
    Task InitializeAsync(Func<AdminRecord?, AdminRecord?> bootstrapAdmin, CancellationToken ct);

    /// <summary>Throws if the database doesn't answer.</summary>
    Task PingAsync(CancellationToken ct);

    /// <summary>Items sorted by name; <paramref name="query"/> is a case-insensitive substring.</summary>
    Task<Page<Item>> ListItemsAsync(string? query, int page, int pageSize, CancellationToken ct);

    Task<Item?> GetItemAsync(Guid id, CancellationToken ct);

    Task<ItemWrite> CreateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct);

    Task<ItemWrite> UpdateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct);

    /// <summary>Returns false if the item didn't exist. Orders are never touched (FR-003).</summary>
    Task<bool> DeleteItemAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Atomically checks every line (exists, price equal, stock enough), then decrements stock and
    /// stores the order, or changes nothing and returns the problems (research R9).
    /// </summary>
    Task<PlaceOrderResult> PlaceOrderAsync(NewOrder order, CancellationToken ct);

    /// <summary>Orders newest first.</summary>
    Task<Page<Order>> ListOrdersAsync(int page, int pageSize, CancellationToken ct);

    Task<AdminRecord?> GetAdminAsync(CancellationToken ct);

    /// <summary>Atomically applies <see cref="LockoutPolicy.ApplyFailure"/> and returns the new record.</summary>
    Task<AdminRecord> RecordFailedSignInAsync(LockoutPolicy policy, DateTimeOffset now, CancellationToken ct);

    Task ResetFailedSignInsAsync(CancellationToken ct);

    /// <summary>
    /// Stores a new hash, sets <c>changedAt</c> and <c>initial=false</c>, and atomically increments
    /// the session version. Returns the new session version.
    /// </summary>
    Task<long> SetAdminPasswordAsync(string passwordHash, DateTimeOffset changedAt, CancellationToken ct);

    /// <summary>Atomically increments the session version (sign-out) and returns the new value.</summary>
    Task<long> BumpSessionVersionAsync(CancellationToken ct);

    Task<IReadOnlyList<DataProtectionKey>> GetDataProtectionKeysAsync(CancellationToken ct);

    /// <summary>Inserts or replaces a Data-Protection key by name.</summary>
    Task StoreDataProtectionKeyAsync(string name, string xml, CancellationToken ct);
}

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, long Total);

public enum ItemWriteStatus
{
    Ok,
    NotFound,
    NameExists,
}

public sealed record ItemWrite(ItemWriteStatus Status, Item? Item = null);

public static class ProblemKind
{
    public const string Removed = "removed";
    public const string Price = "price";
    public const string Stock = "stock";
}

/// <summary>Why one requested line can't be ordered (contracts/http-api.md 409).</summary>
public sealed record OrderProblem(Guid ItemId, string Kind, long? CurrentPriceCents = null, int? CurrentStock = null);

/// <summary>Exactly one of: placed <see cref="Order"/>, <see cref="Problems"/>, or a reference collision.</summary>
public sealed record PlaceOrderResult(Order? Order, IReadOnlyList<OrderProblem> Problems, bool ReferenceTaken = false)
{
    public static PlaceOrderResult Placed(Order order) => new(order, []);

    public static PlaceOrderResult Rejected(IReadOnlyList<OrderProblem> problems) => new(null, problems);

    public static readonly PlaceOrderResult Taken = new(null, [], ReferenceTaken: true);
}

public sealed record DataProtectionKey(string Name, string Xml);

/// <summary>Classifies exceptions that mean "the database doesn't answer" (503, not 500).</summary>
public static class StoreErrors
{
    public static bool IsUnavailable(Exception? ex) => ex switch
    {
        null => false,
        NpgsqlException { IsTransient: true } => true,
        RedisConnectionException or RedisTimeoutException => true,
        TimeoutException or SocketException => true,
        _ => IsUnavailable(ex.InnerException),
    };
}
