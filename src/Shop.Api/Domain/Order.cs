using System.Security.Cryptography;

namespace Shop.Api.Domain;

/// <summary>data-model.md <c>Order.status</c>: PendingPayment → Paid | Expired, nothing else (spec 005).</summary>
public static class OrderStatus
{
    public const string PendingPayment = "PendingPayment";
    public const string Paid = "Paid";
    public const string Expired = "Expired";
}

/// <summary>A stored order line: a snapshot taken at ordering time (data-model.md "OrderLine").</summary>
public sealed record OrderLine(Guid ItemId, string Name, long UnitPriceCents, int Quantity);

/// <summary>
/// data-model.md "Order" (spec 003, extended by spec 005). <see cref="PayingAccount"/> is null for
/// orders placed before 0.3.0: they can never match a payment and simply expire.
/// </summary>
public sealed record Order(
    string Reference,
    DateTimeOffset CreatedAt,
    string Status,
    string WalletAddress,
    long TotalCents,
    IReadOnlyList<OrderLine> Lines,
    string? PayingAccount,
    DateTimeOffset Deadline,
    OrderPayment? Payment = null)
{
    /// <summary>data-model.md <c>Order.total_base_units</c>: the exact mUSDT amount to pay.</summary>
    public System.Numerics.BigInteger TotalBaseUnits => BaseUnits.FromCents(TotalCents);
}

/// <summary>
/// One requested line as sent by the customer. <see cref="ExpectedUnitPriceCents"/> is the price
/// the customer saw. It is only compared with the current price (FR-013, a mismatch rejects the
/// order); stored prices and totals always come from the store, never from the request.
/// </summary>
public sealed record OrderRequestLine(Guid ItemId, int Quantity, long ExpectedUnitPriceCents);

/// <summary>
/// An order ready for the store: reference, time, wallet, paying account (EIP-55) and deadline fixed,
/// lines normalised.
/// </summary>
public sealed record NewOrder(
    string Reference,
    DateTimeOffset CreatedAt,
    string WalletAddress,
    IReadOnlyList<OrderRequestLine> Lines,
    string PayingAccount,
    DateTimeOffset Deadline);

/// <summary>Rules for data-model.md <c>Order.lines</c> and <c>OrderLine.quantity</c>.</summary>
public static class OrderRules
{
    public const int MaxLines = 50;

    /// <summary>
    /// Most pieces of one item in one order (after merging duplicates). Limits how much stock a
    /// single request can reserve while unpaid orders still hold stock (released in spec 005).
    /// </summary>
    public const int MaxQuantityPerLine = 1000;

    /// <summary>
    /// Largest order total (2^53 − 1 cents) that every store computes exactly. With the line and
    /// quantity caps the largest possible total is 50 × 1,000 × <see cref="Money.MaxPriceCents"/>
    /// ≈ 5 × 10^15, below this; the check stays as a guard if the caps change.
    /// </summary>
    public const long MaxTotalCents = 9_007_199_254_740_991;

    /// <summary>
    /// Checks 1–50 lines, quantity 1–1,000 and price ≥ 1, then merges duplicate item ids (summing
    /// the quantities, keeping first-seen order). Different prices for the same item are rejected.
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

        if (lines.Any(l => l.Quantity is < 1 or > MaxQuantityPerLine))
        {
            errors["lines.quantity"] = [QuantityMessage];
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

            var quantity = existing.Quantity + line.Quantity; // each ≤ 1,000, can't overflow
            if (quantity > MaxQuantityPerLine)
            {
                errors["lines.quantity"] = [QuantityMessage];
                return (null, errors);
            }

            merged[i] = existing with { Quantity = quantity };
        }

        var total = merged.Aggregate(Int128.Zero, (sum, l) => sum + ((Int128)l.ExpectedUnitPriceCents * l.Quantity));
        if (total > MaxTotalCents)
        {
            errors["lines"] = ["The order total is too large."];
            return (null, errors);
        }

        return (merged, errors);
    }

    private static readonly string QuantityMessage = $"Quantity must be between 1 and {MaxQuantityPerLine} per item.";
}

/// <summary>data-model.md <c>Order.reference</c>: 10 Crockford base32 characters from a CSPRNG.</summary>
public static class OrderReference
{
    public const int Length = 10;
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);
}
