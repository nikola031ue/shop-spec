using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Endpoints;

/// <summary>
/// The operator's internal API (spec 005 contracts/shop-internal-api.md). Three layers: it is served
/// only on connections accepted by the internal listener (never routed by the Ingress), the
/// NetworkPolicy admits that port only from the operator, and every request needs the bearer token.
/// </summary>
public static partial class InternalEndpoints
{
    /// <summary>Connection item set by the internal listener; nothing else can set it.</summary>
    private const string ConnectionMark = "shop.internal";

    /// <summary>
    /// With an internal token: listen explicitly on the public port and on the internal port, whose
    /// connections are marked. Without one: Kestrel's defaults, no internal listener.
    /// </summary>
    public static void ConfigureListeners(KestrelServerOptions options)
    {
        var settings = options.ApplicationServices.GetRequiredService<Settings>();
        if (settings.InternalToken is null)
        {
            return;
        }

        options.ListenAnyIP(settings.PublicPort);
        options.ListenAnyIP(settings.InternalPort, listen => listen.Use(next => connection =>
        {
            connection.Items[ConnectionMark] = true;
            return next(connection);
        }));
    }

    public static bool IsInternalConnection(HttpContext context) =>
        context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(ConnectionMark) == true;

    /// <summary>The internal listener serves nothing but <c>/internal</c>.</summary>
    public static IApplicationBuilder UseInternalListenerScope(this IApplicationBuilder app) => app.Use((context, next) =>
        IsInternalConnection(context) && !context.Request.Path.StartsWithSegments("/internal")
            ? Results.NotFound().ExecuteAsync(context)
            : next(context));

    public static IEndpointRouteBuilder MapInternalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal").AddEndpointFilter(RequireInternalAccess);
        group.MapPost("/chain", ReceiveChainAsync);
        group.MapGet("/wallets/{address}/waiting", CountWaitingAsync);

        // Unknown /internal paths are 404s on every port, never the Angular app.
        app.MapFallback("/internal/{**path}", () => Results.NotFound());
        return app;
    }

    private static async ValueTask<object?> RequireInternalAccess(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var settings = http.RequestServices.GetRequiredService<Settings>();
        if (settings.InternalToken is null || !IsInternalConnection(http))
        {
            return Results.NotFound();
        }

        var header = http.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
                SHA256.HashData(Encoding.UTF8.GetBytes(settings.InternalToken))))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized.");
        }

        return await next(context);
    }

    public sealed record SeenThroughRequest(long? Block, DateTimeOffset? Time);

    public sealed record TransferRequest(
        string? Tx, int? LogIndex, long? Block, DateTimeOffset? BlockTime, string? From, string? To, string? Value);

    public sealed record ChainRequest(
        string? Address, long? FromBlock, SeenThroughRequest? SeenThrough, List<TransferRequest>? Transfers);

    /// <summary>
    /// Records every transfer in block order (each paid or unmatched, idempotent), then moves the
    /// address's chain progress forward. 204 only when all of it is stored, so the operator advances
    /// its cursor only then.
    /// </summary>
    private static async Task<IResult> ReceiveChainAsync(
        ChainRequest request, IShopStore store, Settings settings, TimeProvider time, ILogger<ChainRequest> logger,
        CancellationToken ct)
    {
        var address = Eip55.Normalize(request.Address, out _);
        if (address is null || request.SeenThrough is not { Block: >= 0, Time: { } seenTime } seen)
        {
            return Invalid("address and seenThrough (block, time) are required.");
        }

        if (address != settings.WalletAddress && !await store.IsKnownAddressAsync(address, ct))
        {
            return Invalid("This address is not one of the shop's addresses.");
        }

        var transfers = new List<ChainTransfer>();
        foreach (var t in request.Transfers ?? [])
        {
            var tx = TxHash.Normalize(t.Tx);
            var from = Eip55.Normalize(t.From, out _);
            var to = Eip55.Normalize(t.To, out _);
            if (tx is null || t.LogIndex is not >= 0 || t.Block is not >= 0 || t.BlockTime is not { } blockTime
                || from is null || to != address || !BaseUnits.TryParse(t.Value, out var value) || t.Block > seen.Block)
            {
                return Invalid("Every transfer needs tx, logIndex, block (≤ seenThrough), blockTime, from, to = address and value.");
            }

            transfers.Add(new ChainTransfer(tx, t.LogIndex.Value, t.Block.Value, blockTime, from, to, value));
        }

        int paid = 0, unmatched = 0;
        foreach (var t in transfers.OrderBy(t => t.Block).ThenBy(t => t.LogIndex))
        {
            switch (await store.RecordTransferAsync(t, time.GetUtcNow(), ct))
            {
                case TransferOutcome.Paid:
                    paid++;
                    break;
                case TransferOutcome.Unmatched:
                    unmatched++;
                    break;
            }
        }

        await store.AdvanceChainProgressAsync(new ChainProgress(address, seen.Block!.Value, seenTime), ct);
        if (paid + unmatched > 0)
        {
            LogTransfers(logger, address, paid, unmatched, seen.Block!.Value);
        }

        return Results.NoContent();
    }

    /// <summary>D-1: orders to this address still waiting; the operator keeps an old Wallet until 0.</summary>
    private static async Task<IResult> CountWaitingAsync(string address, IShopStore store, CancellationToken ct)
    {
        var normalized = Eip55.Normalize(address, out _);
        return normalized is null
            ? Invalid("address must be an Ethereum address.")
            : Results.Ok(new { address = normalized, waiting = await store.CountWaitingAsync(normalized, ct) });
    }

    private static IResult Invalid(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request.", detail: detail);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Recorded transfers to {Address}: {Paid} paid an order, {Unmatched} not matched (seen through block {Block})")]
    private static partial void LogTransfers(ILogger logger, string address, int paid, int unmatched, long block);
}
