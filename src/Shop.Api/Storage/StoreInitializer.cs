using Microsoft.AspNetCore.Identity;
using Shop.Api.Auth;

namespace Shop.Api.Storage;

/// <summary>
/// Runs <see cref="IShopStore.InitializeAsync"/> before Kestrel listens (hosted services start
/// first), retrying with backoff capped at 10 s. Until it succeeds the port stays closed, so the
/// operator's TCP readiness probe fails (research R12).
/// </summary>
public sealed partial class StoreInitializer(
    IServiceProvider services,
    TimeProvider time,
    ILogger<StoreInitializer> logger) : IHostedService
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolving these validates the configuration; a SettingsException stops the start.
        var settings = services.GetRequiredService<Settings>();
        var store = services.GetRequiredService<IShopStore>();
        var hasher = services.GetRequiredService<IPasswordHasher<AdminUser>>();

        var delay = FirstDelay;
        string? lastError = null;
        while (true)
        {
            try
            {
                await store.InitializeAsync(
                    existing => AdminAuth.Bootstrap(existing, settings.AdminPassword, hasher), cancellationToken);
                LogReady(logger, store.GetType().Name);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var error = ex.GetType().Name + ": " + Redaction.Redact(ex.Message);
                if (error != lastError)
                {
                    LogWaiting(logger, error);
                    lastError = error;
                }

                await Task.Delay(delay, time, cancellationToken);
                delay = NextDelay(delay);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static TimeSpan NextDelay(TimeSpan delay) => delay * 2 < MaxDelay ? delay * 2 : MaxDelay;

    [LoggerMessage(Level = LogLevel.Information, Message = "Store ready ({Store})")]
    private static partial void LogReady(ILogger logger, string store);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Waiting for the database, retrying: {Error}")]
    private static partial void LogWaiting(ILogger logger, string error);
}
