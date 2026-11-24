using System.Text.Json.Serialization;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Endpoints;

/// <summary>Public storefront API (contracts/http-api.md "Public"). No sign-in.</summary>
public static class PublicEndpoints
{
    /// <summary>Reference collisions are astronomically rare (32^10); a few retries are plenty.</summary>
    private const int ReferenceAttempts = 5;

    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/shop", (Settings settings) => Results.Ok(new ShopDto(settings.ShopName)));
        api.MapGet("/items", AdminEndpoints.ListItemsAsync);
        api.MapPost("/orders", PlaceOrderAsync);
        return app;
    }

    public sealed record ShopDto(string Name);

    public sealed record OrderLineRequest(Guid ItemId, int Quantity, long UnitPriceCents);

    public sealed record OrderRequest(List<OrderLineRequest>? Lines);

    public sealed record PlacedOrderDto(string Reference, string Status, long TotalCents, DateTimeOffset CreatedAt);

    public sealed record ProblemDto(
        Guid ItemId,
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CurrentPriceCents,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? CurrentStock);

    /// <summary>
    /// Places an order at the shop's current prices. <c>unitPriceCents</c> is the price the
    /// customer saw: any difference is a 409 <c>price</c> problem, and it is never stored (FR-013).
    /// </summary>
    private static async Task<IResult> PlaceOrderAsync(
        OrderRequest request, IShopStore store, Settings settings, TimeProvider time, CancellationToken ct)
    {
        var (lines, errors) = OrderRules.Normalize(
            request.Lines?.Select(l => new OrderRequestLine(l.ItemId, l.Quantity, l.UnitPriceCents)).ToList());
        if (lines is null)
        {
            return Results.ValidationProblem(errors);
        }

        for (var attempt = 0; attempt < ReferenceAttempts; attempt++)
        {
            var result = await store.PlaceOrderAsync(
                new NewOrder(OrderReference.New(), time.GetUtcNow(), settings.WalletAddress, lines), ct);
            if (result.ReferenceTaken)
            {
                continue;
            }

            if (result.Order is { } order)
            {
                return Results.Json(
                    new PlacedOrderDto(order.Reference, order.Status, order.TotalCents, order.CreatedAt),
                    statusCode: StatusCodes.Status201Created);
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
}
