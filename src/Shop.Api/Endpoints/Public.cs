using System.Text.Json.Serialization;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Endpoints;

/// <summary>
/// Public storefront API (spec 003 contracts/http-api.md "Public"; spec 005 contracts/shop-api.md).
/// No sign-in.
/// </summary>
public static class PublicEndpoints
{
    /// <summary>Reference collisions are astronomically rare (32^10); a few retries are plenty.</summary>
    private const int ReferenceAttempts = 5;

    /// <summary>What one mint call may create: 1,000 mUSDT (contracts/musdt.md).</summary>
    public const string MintPerCall = "1000000000";

    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/shop", GetShopAsync);
        api.MapGet("/items", AdminEndpoints.ListItemsAsync);
        api.MapPost("/orders", PlaceOrderAsync).RequireRateLimiting(OrderRateLimit.Policy);
        api.MapGet("/orders/{reference}", GetOrderAsync);
        return app;
    }

    /// <summary>
    /// <see cref="AdminPasswordChanged"/> is false while the operator-generated password is still the
    /// admin password (ShopHub's admin hand-off, spec 004 FR-016). <see cref="Payments"/> tells the
    /// storefront which token and chain to use (spec 005).
    /// </summary>
    public sealed record ShopDto(string Name, bool AdminPasswordChanged, PaymentsDto Payments);

    public sealed record PaymentsDto(string? TokenAddress, long ChainId, string ExplorerTxUrl, string MintPerCall);

    private static async Task<IResult> GetShopAsync(Settings settings, IShopStore store, CancellationToken ct) =>
        Results.Ok(new ShopDto(
            settings.ShopName,
            await store.GetAdminAsync(ct) is { Initial: false },
            new PaymentsDto(settings.TokenAddress, settings.ChainId, settings.ExplorerTxUrl, MintPerCall)));

    public sealed record OrderLineRequest(Guid ItemId, int Quantity, long UnitPriceCents);

    public sealed record OrderRequest(List<OrderLineRequest>? Lines, string? PayingAccount);

    public sealed record PublicOrderLineDto(string Name, long UnitPriceCents, int Quantity);

    public sealed record PaymentDto(string Tx, long Block, DateTimeOffset PaidAt, string ExplorerUrl);

    /// <summary>contracts/shop-api.md <c>Order</c> (public). Amounts in base units are strings.</summary>
    public sealed record OrderDto(
        string Reference,
        string Status,
        IReadOnlyList<PublicOrderLineDto> Lines,
        long TotalCents,
        string TotalBaseUnits,
        string WalletAddress,
        string? PayingAccount,
        DateTimeOffset CreatedAt,
        DateTimeOffset Deadline,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PaymentDto? Payment)
    {
        public static OrderDto From(Order o, Settings settings) => new(
            o.Reference,
            o.Status,
            o.Lines.Select(l => new PublicOrderLineDto(l.Name, l.UnitPriceCents, l.Quantity)).ToList(),
            o.TotalCents,
            BaseUnits.Format(o.TotalBaseUnits),
            o.WalletAddress,
            o.PayingAccount,
            o.CreatedAt,
            o.Deadline,
            o.Payment is { } p ? new PaymentDto(p.Tx, p.Block, p.PaidAt, settings.ExplorerTxUrl + p.Tx) : null);
    }

    public sealed record ProblemDto(
        Guid ItemId,
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CurrentPriceCents,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CurrentStock);

    /// <summary>
    /// Places an order at the shop's current prices for the buyer's connected MetaMask account
    /// (spec 005 FR-002a). <c>unitPriceCents</c> is the price the customer saw: any difference is a
    /// 409 <c>price</c> problem, and it is never stored (FR-013). One waiting order per account
    /// (FR-005a): a second one is 409 <c>waitingOrder</c> naming the first.
    /// </summary>
    private static async Task<IResult> PlaceOrderAsync(
        OrderRequest request, IShopStore store, Settings settings, TimeProvider time, CancellationToken ct)
    {
        var (lines, errors) = OrderRules.Normalize(
            request.Lines?.Select(l => new OrderRequestLine(l.ItemId, l.Quantity, l.UnitPriceCents)).ToList());
        var account = Eip55.Normalize(request.PayingAccount, out _);
        if (account is null)
        {
            errors["payingAccount"] = ["Connect MetaMask to choose the account that pays for this order."];
        }

        if (lines is null || account is null)
        {
            return Results.ValidationProblem(errors);
        }

        if (!settings.PaymentsEnabled)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Payments are not available in this shop yet. Nothing was ordered.",
                extensions: new Dictionary<string, object?> { ["reason"] = "paymentsUnavailable" });
        }

        for (var attempt = 0; attempt < ReferenceAttempts; attempt++)
        {
            var now = time.GetUtcNow();
            var result = await store.PlaceOrderAsync(
                new NewOrder(OrderReference.New(), now, settings.WalletAddress, lines, account,
                    now.AddMinutes(settings.OrderPaymentMinutes)),
                ct);
            if (result.ReferenceTaken)
            {
                continue;
            }

            if (result.Order is { } order)
            {
                return Results.Json(OrderDto.From(order, settings), statusCode: StatusCodes.Status201Created);
            }

            if (result.WaitingOrder is { } waiting)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: $"This account already has order {waiting} waiting for payment. Pay it or wait until it expires.",
                    extensions: new Dictionary<string, object?> { ["reason"] = "waitingOrder", ["reference"] = waiting });
            }

            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Some items changed. Nothing was ordered.",
                extensions: new Dictionary<string, object?>
                {
                    ["problems"] = result.Problems
                        .Select(p => new ProblemDto(p.ItemId, p.Kind, p.CurrentPriceCents, p.CurrentStock))
                        .ToList(),
                });
        }

        throw new InvalidOperationException("Could not allocate a unique order reference.");
    }

    /// <summary>The order page's data; the reference is the only way in (spec 003, 005 FR-002).</summary>
    private static async Task<IResult> GetOrderAsync(string reference, IShopStore store, Settings settings, CancellationToken ct)
    {
        if (reference.Length != OrderReference.Length || !reference.All(OrderReference.Alphabet.Contains)
            || await store.GetOrderAsync(reference, ct) is not { } order)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.");
        }

        return Results.Ok(OrderDto.From(order, settings));
    }
}
