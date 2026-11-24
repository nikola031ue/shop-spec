namespace Shop.Api.Domain;

/// <summary>The single admin record (data-model.md "Admin"). The hash never leaves the server.</summary>
public sealed record AdminRecord(
    string PasswordHash,
    bool Initial,
    DateTimeOffset? ChangedAt,
    int FailedCount,
    DateTimeOffset? FailedWindowStart,
    DateTimeOffset? LockedUntil);

/// <summary>
/// FR-006 lockout: <see cref="MaxFailures"/> failures within <see cref="Window"/> (counted from the
/// first failure) lock sign-in for <see cref="LockFor"/>. Locking resets the counter.
/// </summary>
public sealed record LockoutPolicy(int MaxFailures, TimeSpan Window, TimeSpan LockFor)
{
    public static readonly LockoutPolicy Default = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    public bool IsLocked(AdminRecord admin, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = admin.LockedUntil is { } until && until > now ? until - now : TimeSpan.Zero;
        return retryAfter > TimeSpan.Zero;
    }

    /// <summary>Records one failed sign-in. The Redis store mirrors this in <c>failed_signin.lua</c>.</summary>
    public AdminRecord ApplyFailure(AdminRecord admin, DateTimeOffset now)
    {
        if (IsLocked(admin, now, out _))
        {
            return admin;
        }

        var newWindow = admin.FailedWindowStart is not { } start || now - start >= Window;
        var count = newWindow ? 1 : admin.FailedCount + 1;
        var windowStart = newWindow ? now : admin.FailedWindowStart;
        return count >= MaxFailures
            ? admin with { FailedCount = 0, FailedWindowStart = null, LockedUntil = now + LockFor }
            : admin with { FailedCount = count, FailedWindowStart = windowStart };
    }
}

/// <summary>Both stores keep times with millisecond precision, so their results compare equal.</summary>
public static class StoreTime
{
    public static DateTimeOffset Truncate(DateTimeOffset t) =>
        DateTimeOffset.FromUnixTimeMilliseconds(t.ToUnixTimeMilliseconds());
}
