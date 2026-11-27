using Shop.Api.Storage;

namespace Shop.Api.Payments;

/// <summary>
/// Expires unpaid orders every 15 s (spec 005 FR-009, FR-011, research R5). Every shop copy runs it;
/// the store's conditional state change makes each expiry happen exactly once, and an order expires
/// only once the chain has been read past its deadline, so an outage pauses expiry instead of
/// expiring orders that may have been paid.
/// </summary>
public sealed partial class ExpiryJob(IShopStore store, TimeProvider time, ILogger<ExpiryJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>Orders expired per store call; a backlog drains over a few calls in one tick.</summary>
    public const int Batch = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tick: expires due orders until none are left (or a batch comes back short).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var total = 0;
        int expired;
        do
        {
            expired = await store.ExpireDueOrdersAsync(time.GetUtcNow(), Batch, ct);
            total += expired;
        }
        while (expired == Batch);

        if (total > 0)
        {
            LogExpired(logger, total);
        }

        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} unpaid orders and returned their stock")]
    private static partial void LogExpired(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expiring unpaid orders failed ({Error}); retrying in 15 s")]
    private static partial void LogFailed(ILogger logger, string error);
}
