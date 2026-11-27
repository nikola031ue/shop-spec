using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Shop.Api.Domain;

/// <summary>mUSDT amounts on the chain (spec 005 research R7): 6 decimals, so 1 cent is 10^4 base units.</summary>
public static class BaseUnits
{
    public const int Decimals = 6;

    public static readonly BigInteger PerCent = 10_000;

    public static BigInteger FromCents(long cents) => cents * PerCent;

    /// <summary>Parses a non-negative decimal integer string (a uint256 value) without sign or spaces.</summary>
    public static bool TryParse(string? text, out BigInteger value)
    {
        value = BigInteger.Zero;
        return text is { Length: > 0 and <= 78 } && text.All(char.IsAsciiDigit)
            && BigInteger.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public static string Format(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The payment that settled an order (data-model.md <c>Order.paid_*</c>).</summary>
public sealed record OrderPayment(string Tx, int LogIndex, long Block, DateTimeOffset PaidAt);

/// <summary>
/// A confirmed mUSDT transfer to one of the shop's addresses, as delivered by the shop-operator
/// (contracts/shop-internal-api.md). Addresses are EIP-55.
/// </summary>
public sealed record ChainTransfer(
    string Tx, int LogIndex, long Block, DateTimeOffset BlockTime, string From, string To, BigInteger Value);

/// <summary>data-model.md <c>Payment.unmatched_reason</c>.</summary>
public static class UnmatchedReason
{
    /// <summary>The account's waiting order has another total.</summary>
    public const string Amount = "amount";

    /// <summary>The account has no order in this shop.</summary>
    public const string Account = "account";

    /// <summary>The order exists but expired, or the transfer's block is past its deadline (FR-010).</summary>
    public const string Late = "late";

    /// <summary>The order is already paid.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>Anything else (e.g. sent before the order was placed, or to another of the shop's addresses).</summary>
    public const string Unknown = "unknown";
}

/// <summary>A recorded transfer (data-model.md "Payment").</summary>
public sealed record Payment(
    string Tx,
    int LogIndex,
    long Block,
    DateTimeOffset BlockTime,
    string FromAccount,
    string ToAddress,
    BigInteger Value,
    string? OrderReference,
    string? UnmatchedReason,
    DateTimeOffset ReceivedAt);

public enum TransferOutcome
{
    /// <summary>The transfer paid its order.</summary>
    Paid,

    /// <summary>Recorded as not matched (with a reason).</summary>
    Unmatched,

    /// <summary>Already recorded earlier (same tx hash and log index); nothing changed.</summary>
    AlreadyRecorded,
}

/// <summary>How far the chain has been read for one address (data-model.md "Chain progress").</summary>
public sealed record ChainProgress(string Address, long SeenThroughBlock, DateTimeOffset SeenThroughTime);

/// <summary>Transaction hashes: 0x + 64 hex digits, stored lowercase.</summary>
public static partial class TxHash
{
    public static string? Normalize(string? tx) =>
        tx is not null && Format().IsMatch(tx) ? tx.ToLowerInvariant() : null;

    [GeneratedRegex("^0x[0-9a-fA-F]{64}$")]
    private static partial Regex Format();
}

/// <summary>
/// The matching rule (spec 005 FR-005, research R6; contracts/shop-internal-api.md). PostgreSQL
/// calls it inside its transaction; the Redis script <c>record_transfer.lua</c> implements the same
/// rule and the shared store contract tests hold both to it.
/// </summary>
public static class PaymentMatching
{
    /// <param name="waiting">The paying account's order still waiting for payment, if any (at most one).</param>
    /// <param name="latest">Without a waiting order: the account's latest order to <c>t.To</c>, if any.</param>
    /// <returns>The order the transfer pays, or the reason it is not matched.</returns>
    public static (Order? Paid, string? Reason) Decide(ChainTransfer t, Order? waiting, Order? latest)
    {
        if (waiting is not null)
        {
            if (waiting.WalletAddress != t.To || t.BlockTime < waiting.CreatedAt)
            {
                return (null, UnmatchedReason.Unknown);
            }

            if (waiting.TotalBaseUnits != t.Value)
            {
                return (null, UnmatchedReason.Amount);
            }

            return t.BlockTime > waiting.Deadline ? (null, UnmatchedReason.Late) : (waiting, null);
        }

        if (latest is null)
        {
            return (null, UnmatchedReason.Account);
        }

        if (latest.TotalBaseUnits != t.Value)
        {
            return (null, UnmatchedReason.Unknown);
        }

        return latest.Status switch
        {
            OrderStatus.Paid => (null, UnmatchedReason.Duplicate),
            OrderStatus.Expired => (null, UnmatchedReason.Late),
            _ => (null, UnmatchedReason.Unknown),
        };
    }
}
