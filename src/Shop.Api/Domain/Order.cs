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

/// <summary>One requested line as sent by the customer.</summary>
public sealed record OrderRequestLine(Guid ItemId, int Quantity, long UnitPriceCents);

/// <summary>An order ready for the store: reference, time and wallet fixed, lines normalised.</summary>
public sealed record NewOrder(
    string Reference,
    DateTimeOffset CreatedAt,
    string WalletAddress,
    IReadOnlyList<OrderRequestLine> Lines)
{
    public long TotalCents => Lines.Sum(l => checked(l.UnitPriceCents * l.Quantity));
}

/// <summary>Rules for data-model.md <c>Order.lines</c> and <c>OrderLine.quantity</c>.</summary>
public static class OrderRules
{
    public const int MaxLines = 50;

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

        if (lines.Any(l => l.UnitPriceCents is < 1 or > Money.MaxPriceCents))
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
            if (existing.UnitPriceCents != line.UnitPriceCents)
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
