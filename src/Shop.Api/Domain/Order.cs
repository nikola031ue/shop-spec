using System.Security.Cryptography;

namespace Shop.Api.Domain;

public static class OrderStatus
{
    /// <summary>The only status in this feature; spec 005 adds the rest.</summary>
    public const string PendingPayment = "PendingPayment";
}

/// <summary>A stored order line: a snapshot taken at ordering time (data-model.md "OrderLine").</summary>
public sealed record OrderLine(Guid ItemId, string Name, long UnitPriceCents, int Quantity);

/// <summary>data-model.md "Order".</summary>
public sealed record Order(
    string Reference,
    DateTimeOffset CreatedAt,
    string Status,
    string WalletAddress,
    long TotalCents,
    IReadOnlyList<OrderLine> Lines);

/// <summary>
/// One requested line as sent by the customer. <see cref="ExpectedUnitPriceCents"/> is the price
/// the customer saw. It is only compared with the current price (FR-013, a mismatch rejects the
/// order); stored prices and totals always come from the store, never from the request.
/// </summary>
public sealed record OrderRequestLine(Guid ItemId, int Quantity, long ExpectedUnitPriceCents);

/// <summary>An order ready for the store: reference, time and wallet fixed, lines normalised.</summary>
public sealed record NewOrder(
    string Reference,
    DateTimeOffset CreatedAt,
    string WalletAddress,
    IReadOnlyList<OrderRequestLine> Lines);

/// <summary>Rules for data-model.md <c>Order.lines</c> and <c>OrderLine.quantity</c>.</summary>
public static class OrderRules
{
    public const int MaxLines = 50;

    /// <summary>
    /// Largest order total (2^53 − 1 cents) that every store computes exactly. An order is placed
    /// only when all expected prices equal the current ones, so checking the expected total is
    /// enough.
    /// </summary>
    public const long MaxTotalCents = 9_007_199_254_740_991;

    /// <summary>
    /// Checks 1–50 lines, quantity ≥ 1 and price ≥ 1, then merges duplicate item ids (summing the
    /// quantities, keeping first-seen order). Different prices for the same item are rejected.
    /// </summary>
    public static (IReadOnlyList<OrderRequestLine>? Lines, Dictionary<string, string[]> Errors) Normalize(
        IReadOnlyList<OrderRequestLine>? lines)
    {
        var errors = new Dictionary<string, string[]>();
        if (lines is null || lines.Count is 0 or > MaxLines)
        {
            errors["lines"] = [$"An order needs 1-{MaxLines} lines."];
            return (null, errors);
        }

        if (lines.Any(l => l.Quantity < 1))
        {
            errors["lines.quantity"] = ["Quantity must be at least 1."];
        }

        if (lines.Any(l => l.ExpectedUnitPriceCents is < 1 or > Money.MaxPriceCents))
        {
            errors["lines.unitPriceCents"] = ["Unit price is out of range."];
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var merged = new List<OrderRequestLine>();
        var index = new Dictionary<Guid, int>();
        foreach (var line in lines)
        {
            if (!index.TryGetValue(line.ItemId, out var i))
            {
                index[line.ItemId] = merged.Count;
                merged.Add(line);
                continue;
            }

            var existing = merged[i];
            if (existing.ExpectedUnitPriceCents != line.ExpectedUnitPriceCents)
            {
                errors["lines.unitPriceCents"] = ["The same item appears with different prices."];
                return (null, errors);
            }

            var quantity = (long)existing.Quantity + line.Quantity;
            if (quantity > int.MaxValue)
            {
                errors["lines.quantity"] = ["Quantity is too large."];
                return (null, errors);
            }

            merged[i] = existing with { Quantity = (int)quantity };
        }

        // Int128: 50 lines × (2^31 − 1) × MaxPriceCents would overflow a long.
        var total = merged.Aggregate(Int128.Zero, (sum, l) => sum + ((Int128)l.ExpectedUnitPriceCents * l.Quantity));
        if (total > MaxTotalCents)
        {
            errors["lines"] = ["The order total is too large."];
            return (null, errors);
        }

        return (merged, errors);
    }
}

/// <summary>data-model.md <c>Order.reference</c>: 10 Crockford base32 characters from a CSPRNG.</summary>
public static class OrderReference
{
    public const int Length = 10;
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);
}
