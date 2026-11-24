using System.Globalization;
using System.Text.RegularExpressions;

namespace Shop.Api.Domain;

/// <summary>mUSDT amounts as integer cents (data-model.md).</summary>
public static partial class Money
{
    /// <summary>Highest accepted price, 999,999,999.99. Keeps every stored number exact in both stores.</summary>
    public const long MaxPriceCents = 99_999_999_999;

    /// <summary>Parses a decimal string with at most 2 decimals ("15", "15.5", "15.00") into cents ≥ 1.</summary>
    public static bool TryParsePrice(string? text, out long cents)
    {
        cents = 0;
        if (text is null || !PriceFormat().IsMatch(text))
        {
            return false;
        }

        var parts = text.Split('.');
        var whole = long.Parse(parts[0], CultureInfo.InvariantCulture);
        var fraction = parts.Length == 2 ? long.Parse(parts[1].PadRight(2, '0'), CultureInfo.InvariantCulture) : 0;
        cents = (whole * 100) + fraction;
        return cents is >= 1 and <= MaxPriceCents;
    }

    [GeneratedRegex(@"^[0-9]{1,9}(\.[0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PriceFormat();
}
