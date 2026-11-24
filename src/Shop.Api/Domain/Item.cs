namespace Shop.Api.Domain;

/// <summary>A shop item (data-model.md "Item"). Money is integer cents of mUSDT.</summary>
public sealed record Item(
    Guid Id,
    string Name,
    int Stock,
    long PriceCents,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>FR-012: an item can be ordered only while it has stock.</summary>
    public bool Available => Stock > 0;
}

/// <summary>Validated input for creating or updating an item.</summary>
public sealed record ItemDraft(string Name, int Stock, long PriceCents)
{
    /// <summary>Case-insensitive uniqueness key (data-model.md <c>Item.name</c>).</summary>
    public string NameKey => ItemRules.NameKey(Name);
}

/// <summary>Rules for data-model.md <c>Item.name</c>, <c>Item.stock</c> and <c>Item.priceCents</c>.</summary>
public static class ItemRules
{
    public const int NameMaxLength = 100;

    public static string NameKey(string name) => name.Trim().ToLowerInvariant();

    /// <summary>Validates raw input; errors are keyed by request field name.</summary>
    public static (ItemDraft? Draft, Dictionary<string, string[]> Errors) Validate(string? name, int? stock, string? price)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            errors["name"] = [$"Name must be 1-{NameMaxLength} characters."];
        }

        if (stock is null or < 0)
        {
            errors["stock"] = ["Stock must be a whole number of 0 or more."];
        }

        if (!Money.TryParsePrice(price, out var cents))
        {
            errors["price"] = ["Price must be between 0.01 and 999999999.99 with at most 2 decimals."];
        }

        return errors.Count == 0 ? (new ItemDraft(trimmed, stock!.Value, cents), errors) : (null, errors);
    }
}
