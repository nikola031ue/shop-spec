using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Time.Testing;
using Shop.Api.Auth;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Tests.Unit;

public sealed class AdminAuthTests
{
    private const string Generated = "generated-admin-password";
    private const string AddressA = "203.0.113.7";
    private const string AddressB = "198.51.100.9";
    private readonly PasswordHasher<AdminUser> _hasher = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    private readonly AdminOnlyStore _store = new();

    private AdminAuth CreateAuth()
    {
        _store.Admin = AdminAuth.Bootstrap(null, Generated, _hasher);
        return new AdminAuth(_store, _hasher, _time);
    }

    [Fact]
    public void BootstrapCreatesInitialAdmin()
    {
        var admin = AdminAuth.Bootstrap(null, Generated, _hasher);

        Assert.NotNull(admin);
        Assert.True(admin.Initial);
        Assert.Null(admin.ChangedAt);
        Assert.NotEqual(Generated, admin.PasswordHash);
        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(AdminUser.Instance, admin.PasswordHash, Generated));
    }

    [Fact]
    public void BootstrapLeavesMatchingInitialAdminAlone()
    {
        var admin = AdminAuth.Bootstrap(null, Generated, _hasher);

        Assert.Null(AdminAuth.Bootstrap(admin, Generated, _hasher));
    }

    [Fact]
    public void BootstrapRehashesWhenGeneratedSecretChanged()
    {
        var admin = AdminAuth.Bootstrap(null, Generated, _hasher)!;

        var next = AdminAuth.Bootstrap(admin, "regenerated-password", _hasher);

        Assert.NotNull(next);
        Assert.True(next.Initial);
        Assert.Equal(admin.SessionVersion + 1, next.SessionVersion);
        Assert.Equal(PasswordVerificationResult.Success, _hasher.VerifyHashedPassword(AdminUser.Instance, next.PasswordHash, "regenerated-password"));
    }

    [Fact]
    public void BootstrapNeverTouchesChangedPassword()
    {
        var changed = new AdminRecord(_hasher.HashPassword(AdminUser.Instance, "my-own-password!"), false,
            DateTimeOffset.UnixEpoch);

        Assert.Null(AdminAuth.Bootstrap(changed, Generated, _hasher));
    }

    [Fact]
    public async Task SignInSucceedsWithCorrectPassword()
    {
        var auth = CreateAuth();

        var result = await auth.SignInAsync(Generated, AddressA, TestContext.Current.CancellationToken);

        Assert.Equal(SignInStatus.Success, result.Status);
    }

    [Fact]
    public async Task SixthAttemptIsLockedEvenWithCorrectPasswordUntil15MinutesPass()
    {
        var auth = CreateAuth();
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(SignInStatus.WrongPassword, (await auth.SignInAsync("wrong", AddressA, ct)).Status);
            _time.Advance(TimeSpan.FromSeconds(30));
        }

        var locked = await auth.SignInAsync(Generated, AddressA, ct);
        Assert.Equal(SignInStatus.Locked, locked.Status);
        Assert.Equal(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(30), locked.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(SignInStatus.Locked, (await auth.SignInAsync(Generated, AddressA, ct)).Status);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(SignInStatus.Success, (await auth.SignInAsync(Generated, AddressA, ct)).Status);
        Assert.Equal(LockoutScope.Empty, _store.Scope(AdminAuth.Account, AddressA));
    }

    /// <summary>FR-023: a lock applies to (account, address); the same account from another address signs in.</summary>
    [Fact]
    public async Task LockOnOneAddressDoesNotLockAnother()
    {
        var auth = CreateAuth();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 5; i++)
        {
            await auth.SignInAsync("wrong", AddressA, ct);
        }

        Assert.Equal(SignInStatus.Locked, (await auth.SignInAsync(Generated, AddressA, ct)).Status);
        Assert.Equal(SignInStatus.Success, (await auth.SignInAsync(Generated, AddressB, ct)).Status);
        Assert.Equal(SignInStatus.Locked, (await auth.SignInAsync(Generated, AddressA, ct)).Status);
    }

    /// <summary>FR-023 ceiling: 50 failures across addresses in 15 minutes lock every address.</summary>
    [Fact]
    public async Task FiftyFailuresAcrossAddressesLockTheWholeShop()
    {
        var auth = CreateAuth();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 50; i++)
        {
            // Two failures per address, so no single address reaches its own limit.
            Assert.Equal(SignInStatus.WrongPassword, (await auth.SignInAsync("wrong", $"192.0.2.{i / 2}", ct)).Status);
        }

        var locked = await auth.SignInAsync(Generated, AddressB, ct);
        Assert.Equal(SignInStatus.Locked, locked.Status);
        Assert.Equal(TimeSpan.FromMinutes(15), locked.RetryAfter);

        _time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(SignInStatus.Success, (await auth.SignInAsync(Generated, AddressB, ct)).Status);
    }

    [Fact]
    public async Task FailuresSpreadBeyondTheWindowDoNotLock()
    {
        var auth = CreateAuth();
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(SignInStatus.WrongPassword, (await auth.SignInAsync("wrong", AddressA, ct)).Status);
            _time.Advance(TimeSpan.FromMinutes(4));
        }

        Assert.Equal(SignInStatus.Success, (await auth.SignInAsync(Generated, AddressA, ct)).Status);
    }

    [Fact]
    public async Task SuccessResetsTheFailureCounter()
    {
        var auth = CreateAuth();
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 4; i++)
        {
            await auth.SignInAsync("wrong", AddressA, ct);
        }

        await auth.SignInAsync("wrong", AddressB, ct);

        await auth.SignInAsync(Generated, AddressA, ct);

        // Only this address's scope is cleared; the other address and the ceiling keep counting.
        Assert.Equal(LockoutScope.Empty, _store.Scope(AdminAuth.Account, AddressA));
        Assert.Equal(1, _store.Scope(AdminAuth.Account, AddressB).FailedCount);
        Assert.Equal(5, _store.Scope(LockoutKeys.ShopWide, LockoutKeys.ShopWide).FailedCount);
        Assert.Equal(SignInStatus.WrongPassword, (await auth.SignInAsync("wrong", AddressA, ct)).Status);
        Assert.Equal(1, _store.Scope(AdminAuth.Account, AddressA).FailedCount);
    }

    [Fact]
    public async Task ChangePasswordRules()
    {
        var auth = CreateAuth();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ChangePasswordStatus.TooShort, await auth.ChangePasswordAsync(Generated, "short-11chr", ct));
        Assert.Equal(ChangePasswordStatus.WrongCurrentPassword, await auth.ChangePasswordAsync("wrong", "long-enough-pass", ct));
        Assert.True(await auth.IsInitialPasswordAsync(ct));

        Assert.Equal(ChangePasswordStatus.Success, await auth.ChangePasswordAsync(Generated, "long-enough-pass", ct));

        Assert.False(_store.Admin!.Initial);
        Assert.Equal(_time.GetUtcNow(), _store.Admin.ChangedAt);
        Assert.False(await auth.IsInitialPasswordAsync(ct));
        Assert.Equal(SignInStatus.WrongPassword, (await auth.SignInAsync(Generated, AddressA, ct)).Status);
        Assert.Equal(SignInStatus.Success, (await auth.SignInAsync("long-enough-pass", AddressA, ct)).Status);
    }

    [Fact]
    public void InitRetryBackoffIsCappedAt10Seconds()
    {
        var delays = new List<TimeSpan>();
        var d = StoreInitializer.FirstDelay;
        for (var i = 0; i < 10; i++)
        {
            delays.Add(d);
            d = StoreInitializer.NextDelay(d);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(500), delays[0]);
        Assert.All(delays, x => Assert.True(x <= TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(10), delays[^1]);
    }

    /// <summary>In-memory admin record and lockout scopes; only these members are used by <see cref="AdminAuth"/>.</summary>
    private sealed class AdminOnlyStore : IShopStore
    {
        private readonly Dictionary<(string, string), LockoutScope> _scopes = [];

        public AdminRecord? Admin { get; set; }

        public LockoutScope Scope(string account, string address) =>
            _scopes.GetValueOrDefault((account, address), LockoutScope.Empty);

        public Task<AdminRecord?> GetAdminAsync(CancellationToken ct) => Task.FromResult(Admin);

        public Task<LockoutState> GetLockoutAsync(string account, string address, CancellationToken ct) =>
            Task.FromResult(new LockoutState(Scope(account, address), Scope(LockoutKeys.ShopWide, LockoutKeys.ShopWide)));

        public async Task<LockoutState> RecordFailedSignInAsync(
            LockoutRules rules, string account, string address, DateTimeOffset now, CancellationToken ct)
        {
            var next = (await GetLockoutAsync(account, address, ct)).ApplyFailure(rules, now);
            _scopes[(account, address)] = next.Scope;
            _scopes[(LockoutKeys.ShopWide, LockoutKeys.ShopWide)] = next.ShopWide;
            return next;
        }

        public Task ResetFailedSignInsAsync(string account, string address, CancellationToken ct)
        {
            _scopes.Remove((account, address));
            return Task.CompletedTask;
        }

        public Task<long> SetAdminPasswordAsync(string passwordHash, DateTimeOffset changedAt, CancellationToken ct)
        {
            Admin = Admin! with
            {
                PasswordHash = passwordHash,
                Initial = false,
                ChangedAt = changedAt,
                SessionVersion = Admin.SessionVersion + 1,
            };
            return Task.FromResult(Admin.SessionVersion);
        }

        public Task<long> BumpSessionVersionAsync(CancellationToken ct)
        {
            Admin = Admin! with { SessionVersion = Admin.SessionVersion + 1 };
            return Task.FromResult(Admin.SessionVersion);
        }

        public Task InitializeAsync(Func<AdminRecord?, AdminRecord?> bootstrapAdmin, CancellationToken ct) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Page<Item>> ListItemsAsync(string? query, int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<Item?> GetItemAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemWrite> CreateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemWrite> UpdateItemAsync(Guid id, ItemDraft draft, DateTimeOffset now, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteItemAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<PlaceOrderResult> PlaceOrderAsync(NewOrder order, CancellationToken ct) => throw new NotSupportedException();
        public Task<Page<Order>> ListOrdersAsync(int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<Order?> GetOrderAsync(string reference, CancellationToken ct) => throw new NotSupportedException();
        public Task<TransferOutcome> RecordTransferAsync(ChainTransfer transfer, DateTimeOffset receivedAt, CancellationToken ct) => throw new NotSupportedException();
        public Task AdvanceChainProgressAsync(ChainProgress progress, CancellationToken ct) => throw new NotSupportedException();
        public Task<ChainProgress?> GetChainProgressAsync(string address, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsKnownAddressAsync(string address, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ExpireDueOrdersAsync(DateTimeOffset now, int limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> CountWaitingAsync(string address, CancellationToken ct) => throw new NotSupportedException();
        public Task<Page<Payment>> ListPaymentsAsync(bool unmatchedOnly, int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DataProtectionKey>> GetDataProtectionKeysAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task StoreDataProtectionKeyAsync(string name, string xml, CancellationToken ct) => throw new NotSupportedException();
    }
}
