namespace Shop.Api.Domain;

/// <summary>
/// The single admin record (data-model.md "Admin"). The hash never leaves the server.
/// <see cref="SessionVersion"/> goes up on sign-out and password change; session cookies carry
/// the version they were issued with and are refused once it is older. Lockout state is kept per
/// (account, client address) in <see cref="LockoutScope"/>s (spec 004 FR-023).
/// </summary>
public sealed record AdminRecord(
    string PasswordHash,
    bool Initial,
    DateTimeOffset? ChangedAt,
    long SessionVersion = 1);

/// <summary>
/// Failed sign-ins counted for one scope: an (account, client address) pair, or the shop-wide
/// ceiling (<see cref="LockoutKeys.ShopWide"/>).
/// </summary>
public sealed record LockoutScope(int FailedCount, DateTimeOffset? FailedWindowStart, DateTimeOffset? LockedUntil)
{
    public static readonly LockoutScope Empty = new(0, null, null);

    public bool IsLocked(DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = LockedUntil is { } until && until > now ? until - now : TimeSpan.Zero;
        return retryAfter > TimeSpan.Zero;
    }
}

/// <summary>
/// One fixed-window rule: <see cref="MaxFailures"/> failures within <see cref="Window"/> (counted
/// from the first failure) lock the scope for <see cref="LockFor"/>. Locking resets the counter.
/// </summary>
public sealed record LockoutPolicy(int MaxFailures, TimeSpan Window, TimeSpan LockFor)
{
    /// <summary>FR-023: 5 failures for one account from one address in 15 minutes.</summary>
    public static readonly LockoutPolicy PerAccountAddress = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    /// <summary>FR-023 ceiling against distributed guessing: 50 failures across all addresses.</summary>
    public static readonly LockoutPolicy ShopWide = new(50, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    /// <summary>How long a scope's state matters after its last change.</summary>
    public TimeSpan Retention => Window > LockFor ? Window : LockFor;

    /// <summary>Records one failed sign-in. The Redis store mirrors this in <c>failed_signin.lua</c>.</summary>
    public LockoutScope ApplyFailure(LockoutScope scope, DateTimeOffset now)
    {
        if (scope.IsLocked(now, out _))
        {
            return scope;
        }

        var newWindow = scope.FailedWindowStart is not { } start || now - start >= Window;
        var count = newWindow ? 1 : scope.FailedCount + 1;
        var windowStart = newWindow ? now : scope.FailedWindowStart;
        return count >= MaxFailures
            ? new LockoutScope(0, null, now + LockFor)
            : new LockoutScope(count, windowStart, null);
    }
}

/// <summary>The two rules applied to every failed sign-in (data-model.md "shop app 0.2.0 storage additions").</summary>
public sealed record LockoutRules(LockoutPolicy PerAccountAddress, LockoutPolicy ShopWide)
{
    public static readonly LockoutRules Default = new(LockoutPolicy.PerAccountAddress, LockoutPolicy.ShopWide);
}

/// <summary>The (account, address) scope and the shop-wide scope a sign-in is checked against.</summary>
public sealed record LockoutState(LockoutScope Scope, LockoutScope ShopWide)
{
    public static readonly LockoutState Empty = new(LockoutScope.Empty, LockoutScope.Empty);

    /// <summary>Locked while either scope is locked; the retry time is the later of the two.</summary>
    public bool IsLocked(DateTimeOffset now, out TimeSpan retryAfter)
    {
        Scope.IsLocked(now, out var scope);
        ShopWide.IsLocked(now, out var shopWide);
        retryAfter = scope > shopWide ? scope : shopWide;
        return retryAfter > TimeSpan.Zero;
    }

    /// <summary>
    /// Counts one failure in both scopes, unless either is already locked: then nothing changes.
    /// Attempts that slipped past the lock check (concurrent requests) must not count towards the
    /// ceiling, or one address alone could lock the whole shop. The Redis store mirrors this in
    /// <c>failed_signin.lua</c>.
    /// </summary>
    public LockoutState ApplyFailure(LockoutRules rules, DateTimeOffset now) => IsLocked(now, out _)
        ? this
        : new(rules.PerAccountAddress.ApplyFailure(Scope, now), rules.ShopWide.ApplyFailure(ShopWide, now));
}

public static class LockoutKeys
{
    /// <summary>Account and address of the shop-wide scope; never a real account or address.</summary>
    public const string ShopWide = "*";

    /// <summary>Rejects keys that would collide with the shop-wide scope or are empty.</summary>
    public static void Validate(string account, string address)
    {
        if (string.IsNullOrEmpty(account) || account == ShopWide)
        {
            throw new ArgumentException("Invalid lockout account.", nameof(account));
        }

        if (string.IsNullOrEmpty(address) || address == ShopWide)
        {
            throw new ArgumentException("Invalid lockout address.", nameof(address));
        }
    }
}

/// <summary>Both stores keep times with millisecond precision, so their results compare equal.</summary>
public static class StoreTime
{
    public static DateTimeOffset Truncate(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());
}
