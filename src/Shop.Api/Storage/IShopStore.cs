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
    /// Atomically checks every line (exists, price equal, stock enough) and that the paying account
    /// has no order waiting for payment (spec 005 FR-005a), then decrements stock and stores the
    /// order, or changes nothing and returns why (research R9).
    /// </summary>
    Task<PlaceOrderResult> PlaceOrderAsync(NewOrder order, CancellationToken ct);

    /// <summary>Orders newest first.</summary>
    Task<Page<Order>> ListOrdersAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>One order by its reference (the buyer's handle), or null.</summary>
    Task<Order?> GetOrderAsync(string reference, CancellationToken ct);

    /// <summary>
    /// Records one confirmed transfer in one atomic step: it pays the matching order (spec 005
    /// research R6) or is recorded as not matched with a reason. Idempotent on (tx, log index).
    /// </summary>
    Task<TransferOutcome> RecordTransferAsync(ChainTransfer transfer, DateTimeOffset receivedAt, CancellationToken ct);

    /// <summary>Moves the chain progress for an address forward; never backwards.</summary>
    Task AdvanceChainProgressAsync(ChainProgress progress, CancellationToken ct);

    Task<ChainProgress?> GetChainProgressAsync(string address, CancellationToken ct);

    /// <summary>True when an order was ever placed to this address or progress exists for it.</summary>
    Task<bool> IsKnownAddressAsync(string address, CancellationToken ct);

    /// <summary>
    /// Expires up to <paramref name="limit"/> orders that are waiting, past their deadline and whose
    /// address's chain progress is past the deadline too (research R5), returning their stock in the
    /// same step, each exactly once. Returns how many were expired.
    /// </summary>
    Task<int> ExpireDueOrdersAsync(DateTimeOffset now, int limit, CancellationToken ct);

    /// <summary>Orders to this address still waiting for payment (D-1).</summary>
    Task<long> CountWaitingAsync(string address, CancellationToken ct);

    /// <summary>Recorded transfers newest first (by block time); only unmatched ones when asked.</summary>
    Task<Page<Payment>> ListPaymentsAsync(bool unmatchedOnly, int page, int pageSize, CancellationToken ct);

    Task<AdminRecord?> GetAdminAsync(CancellationToken ct);

    /// <summary>The (account, address) scope and the shop-wide scope; empty when unused or expired.</summary>
    Task<LockoutState> GetLockoutAsync(string account, string address, CancellationToken ct);

    /// <summary>
    /// Atomically applies <see cref="LockoutState.ApplyFailure"/> to the (account, address) scope and
    /// the shop-wide scope, and returns the new state (spec 004 FR-023).
    /// </summary>
    Task<LockoutState> RecordFailedSignInAsync(
        LockoutRules rules, string account, string address, DateTimeOffset now, CancellationToken ct);

    /// <summary>Clears only the (account, address) scope; the shop-wide scope is kept.</summary>
    Task ResetFailedSignInsAsync(string account, string address, CancellationToken ct);

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

/// <summary>
/// Exactly one of: placed <see cref="Order"/>, <see cref="Problems"/>, a reference collision, or the
/// paying account's order that is still waiting (<see cref="WaitingOrder"/>, FR-005a).
/// </summary>
public sealed record PlaceOrderResult(
    Order? Order, IReadOnlyList<OrderProblem> Problems, bool ReferenceTaken = false, string? WaitingOrder = null)
{
    public static PlaceOrderResult Placed(Order order) => new(order, []);

    public static PlaceOrderResult Rejected(IReadOnlyList<OrderProblem> problems) => new(null, problems);

    public static PlaceOrderResult Waiting(string reference) => new(null, [], WaitingOrder: reference);

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
