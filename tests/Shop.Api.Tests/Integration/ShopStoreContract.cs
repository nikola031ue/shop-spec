using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Tests.Integration;

/// <summary>
/// Every behavioural test of <see cref="IShopStore"/> (research R8). Each store subclasses it once
/// with its own Testcontainers database, so both stores pass the identical suite (SC-003). Tests in
/// one class run sequentially; each starts from an empty, initialised store.
/// </summary>
public abstract class ShopStoreContract : IAsyncLifetime
{
    protected static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, 123, TimeSpan.Zero);
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    private const string Admin = "admin";
    private const string AddrA = "203.0.113.7";
    private const string AddrB = "198.51.100.9";

    private readonly List<IShopStore> _stores = [];

    protected IShopStore Store { get; private set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Empties the database used by <see cref="CreateStore"/>.</summary>
    protected abstract Task ResetAsync();

    /// <summary>A new store instance on the shared test database (like another replica).</summary>
    protected abstract IShopStore CreateStore();

    public async ValueTask InitializeAsync()
    {
        await ResetAsync();
        Store = NewStore();
        await Store.InitializeAsync(InitialAdmin, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            if (store is IAsyncDisposable d)
            {
                await d.DisposeAsync();
            }
        }

        GC.SuppressFinalize(this);
    }

    private IShopStore NewStore()
    {
        var store = CreateStore();
        _stores.Add(store);
        return store;
    }

    private static AdminRecord? InitialAdmin(AdminRecord? existing) =>
        existing ?? new AdminRecord("hash-initial", true, null);

    private async Task<Item> AddItemAsync(string name, int stock = 10, long priceCents = 1500)
    {
        var write = await Store.CreateItemAsync(Guid.CreateVersion7(), new ItemDraft(name, stock, priceCents), T0, Ct);
        Assert.Equal(ItemWriteStatus.Ok, write.Status);
        return write.Item!;
    }

    private static NewOrder Order(params OrderRequestLine[] lines) =>
        new(OrderReference.New(), T0.AddMinutes(1), Wallet, lines);

    // ---- Initialisation and admin ----

    [Fact]
    public async Task InitializeCreatesAdminAndIsIdempotent()
    {
        var admin = await Store.GetAdminAsync(Ct);
        Assert.Equal(new AdminRecord("hash-initial", true, null), admin);

        var calls = 0;
        await Store.InitializeAsync(existing =>
        {
            calls++;
            Assert.NotNull(existing);
            return null;
        }, Ct);

        Assert.Equal(1, calls);
        Assert.Equal(admin, await Store.GetAdminAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentInitializeFromThreeReplicasCreatesTheAdminOnce()
    {
        await ResetAsync();
        var created = 0;
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        await Task.WhenAll(replicas.Select(r => Task.Run(() => r.InitializeAsync(existing =>
        {
            if (existing is not null)
            {
                return null;
            }

            Interlocked.Increment(ref created);
            return new AdminRecord("hash-" + Guid.NewGuid(), true, null);
        }, Ct), Ct)));

        Assert.Equal(1, created);
        var admins = await Task.WhenAll(replicas.Select(r => r.GetAdminAsync(Ct)));
        Assert.Single(admins.Distinct());
        Assert.Empty((await replicas[0].ListItemsAsync(null, 1, 50, Ct)).Items);
    }

    [Fact]
    public async Task BootstrapCanReplaceInitialHash()
    {
        await Store.InitializeAsync(existing => existing! with { PasswordHash = "hash-regenerated" }, Ct);

        Assert.Equal("hash-regenerated", (await Store.GetAdminAsync(Ct))!.PasswordHash);
    }

    [Fact]
    public async Task PasswordChangeSetsChangedAtAndClearsInitial()
    {
        var version = await Store.SetAdminPasswordAsync("hash-changed", T0, Ct);

        var admin = await Store.GetAdminAsync(Ct);
        Assert.Equal("hash-changed", admin!.PasswordHash);
        Assert.False(admin.Initial);
        Assert.Equal(T0, admin.ChangedAt);
        Assert.Equal(2, version);
        Assert.Equal(2, admin.SessionVersion);
    }

    [Fact]
    public async Task SessionVersionStartsAtOneAndOnlyGoesUp()
    {
        Assert.Equal(1, (await Store.GetAdminAsync(Ct))!.SessionVersion);

        Assert.Equal(2, await Store.BumpSessionVersionAsync(Ct));
        Assert.Equal(3, await Store.SetAdminPasswordAsync("hash-changed", T0, Ct));

        // Lockout and bootstrap writes keep it.
        await Store.RecordFailedSignInAsync(LockoutRules.Default, Admin, AddrA, T0, Ct);
        await Store.ResetFailedSignInsAsync(Admin, AddrA, Ct);
        await Store.InitializeAsync(_ => null, Ct);
        Assert.Equal(3, (await NewStore().GetAdminAsync(Ct))!.SessionVersion);
    }

    [Fact]
    public async Task ConcurrentSessionBumpsAreAllCounted()
    {
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        var versions = await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            Task.Run(() => replicas[i % 3].BumpSessionVersionAsync(Ct), Ct)));

        Assert.Equal(Enumerable.Range(2, 30).Select(v => (long)v), versions.Order());
        Assert.Equal(31, (await Store.GetAdminAsync(Ct))!.SessionVersion);
    }

    // ---- Lockout per (account, address) with a shop-wide ceiling (spec 004 FR-023) ----

    private static LockoutRules Rules(int perAddress, int shopWide = 1000) => new(
        new LockoutPolicy(perAddress, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)),
        new LockoutPolicy(shopWide, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)));

    [Fact]
    public async Task LockoutStartsEmpty() =>
        Assert.Equal(LockoutState.Empty, await Store.GetLockoutAsync(Admin, AddrA, Ct));

    [Fact]
    public async Task LockoutCountsFailuresAndLocksOnTheFifth()
    {
        var rules = LockoutRules.Default;
        for (var i = 1; i <= 4; i++)
        {
            var state = await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0.AddMinutes(i), Ct);
            Assert.Equal(new LockoutScope(i, T0.AddMinutes(1), null), state.Scope);
            Assert.Equal(new LockoutScope(i, T0.AddMinutes(1), null), state.ShopWide);
        }

        var locked = await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0.AddMinutes(5), Ct);

        Assert.Equal(new LockoutScope(0, null, T0.AddMinutes(20)), locked.Scope);
        Assert.Equal(5, locked.ShopWide.FailedCount);
        Assert.Equal(locked, await NewStore().GetLockoutAsync(Admin, AddrA, Ct));

        // Failures while locked change nothing, not even the shop-wide count.
        var after = await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0.AddMinutes(6), Ct);
        Assert.Equal(locked, after);
    }

    [Fact]
    public async Task LockedAddressDoesNotLockTheSameAccountElsewhere()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.RecordFailedSignInAsync(LockoutRules.Default, Admin, AddrA, T0, Ct);
        }

        Assert.True((await Store.GetLockoutAsync(Admin, AddrA, Ct)).IsLocked(T0, out _));
        var other = await Store.GetLockoutAsync(Admin, AddrB, Ct);
        Assert.False(other.IsLocked(T0, out _));
        Assert.Equal(LockoutScope.Empty, other.Scope);
    }

    /// <summary>
    /// FR-023: failures for one account from address A don't lock a different account from A. The
    /// app has one admin account, so the second account exists only at the store level.
    /// </summary>
    [Fact]
    public async Task FailuresForOneAccountDoNotLockAnotherAccountFromTheSameAddress()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.RecordFailedSignInAsync(LockoutRules.Default, Admin, AddrA, T0, Ct);
        }

        Assert.True((await Store.GetLockoutAsync(Admin, AddrA, Ct)).IsLocked(T0, out _));
        var otherAccount = await Store.GetLockoutAsync("second-account", AddrA, Ct);
        Assert.False(otherAccount.IsLocked(T0, out _));
        Assert.Equal(LockoutScope.Empty, otherAccount.Scope);
    }

    [Fact]
    public async Task FiftyFailuresAcrossAddressesLockTheShop()
    {
        for (var i = 0; i < 50; i++)
        {
            await Store.RecordFailedSignInAsync(LockoutRules.Default, Admin, $"192.0.2.{i / 2}", T0.AddSeconds(i), Ct);
        }

        var fresh = await Store.GetLockoutAsync(Admin, AddrB, Ct);
        Assert.Equal(LockoutScope.Empty, fresh.Scope);
        Assert.Equal(new LockoutScope(0, null, T0.AddSeconds(49).AddMinutes(15)), fresh.ShopWide);
        Assert.True(fresh.IsLocked(T0.AddMinutes(10), out _));
        Assert.False(fresh.IsLocked(T0.AddSeconds(49).AddMinutes(15), out _));
    }

    /// <summary>
    /// FR-023: one address alone can't trigger the shop-wide lock. Attempts that passed the lock
    /// check concurrently (here: 60 at once from three replicas) count only until the pair locks.
    /// </summary>
    [Fact]
    public async Task OneAddressAloneCannotLockTheShop()
    {
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        await Task.WhenAll(Enumerable.Range(0, 60).Select(i =>
            Task.Run(() => replicas[i % 3].RecordFailedSignInAsync(LockoutRules.Default, Admin, AddrA, T0, Ct), Ct)));

        var state = await Store.GetLockoutAsync(Admin, AddrA, Ct);
        Assert.True(state.Scope.IsLocked(T0, out _));
        Assert.Equal(new LockoutScope(5, T0, null), state.ShopWide);
        Assert.False((await Store.GetLockoutAsync(Admin, AddrB, Ct)).IsLocked(T0, out _));
    }

    /// <summary>While the shop-wide scope is locked, failures don't count for any address either.</summary>
    [Fact]
    public async Task FailuresDuringTheShopWideLockCountNowhere()
    {
        var rules = Rules(perAddress: 5, shopWide: 2);
        await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0, Ct);
        await Store.RecordFailedSignInAsync(rules, Admin, AddrB, T0, Ct);

        var state = await Store.RecordFailedSignInAsync(rules, Admin, AddrB, T0.AddMinutes(1), Ct);

        Assert.True(state.ShopWide.IsLocked(T0.AddMinutes(1), out _));
        Assert.Equal(new LockoutScope(1, T0, null), state.Scope);
    }

    [Fact]
    public async Task LockoutWindowStartsAtTheFirstFailure()
    {
        var rules = LockoutRules.Default;
        await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0, Ct);
        await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0.AddMinutes(14), Ct);

        var state = await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0.AddMinutes(15), Ct);

        Assert.Equal(new LockoutScope(1, T0.AddMinutes(15), null), state.Scope);
        Assert.Equal(new LockoutScope(1, T0.AddMinutes(15), null), state.ShopWide);
    }

    [Fact]
    public async Task ConcurrentFailuresAreAllCounted()
    {
        var rules = Rules(100);
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            Task.Run(() => replicas[i % 3].RecordFailedSignInAsync(rules, Admin, i % 2 == 0 ? AddrA : AddrB, T0, Ct), Ct)));

        var a = await Store.GetLockoutAsync(Admin, AddrA, Ct);
        var b = await Store.GetLockoutAsync(Admin, AddrB, Ct);
        Assert.Equal(15, a.Scope.FailedCount);
        Assert.Equal(15, b.Scope.FailedCount);
        Assert.Equal(30, a.ShopWide.FailedCount);
    }

    [Fact]
    public async Task ResetClearsOnlyItsOwnScope()
    {
        var rules = Rules(1);
        await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0, Ct);
        await Store.RecordFailedSignInAsync(rules, Admin, AddrB, T0, Ct);

        await Store.ResetFailedSignInsAsync(Admin, AddrA, Ct);

        var a = await Store.GetLockoutAsync(Admin, AddrA, Ct);
        Assert.Equal(LockoutScope.Empty, a.Scope);
        Assert.Equal(2, a.ShopWide.FailedCount);
        Assert.True((await Store.GetLockoutAsync(Admin, AddrB, Ct)).Scope.IsLocked(T0, out _));
        Assert.Equal("hash-initial", (await Store.GetAdminAsync(Ct))!.PasswordHash);
    }

    [Fact]
    public async Task ExpiredScopesStartOver()
    {
        var rules = LockoutRules.Default;
        for (var i = 0; i < 5; i++)
        {
            await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0, Ct);
        }

        // After the lock and the window are over, a failure counts from one again.
        var later = await Store.RecordFailedSignInAsync(rules, Admin, AddrA, T0.AddMinutes(16), Ct);

        Assert.Equal(new LockoutScope(1, T0.AddMinutes(16), null), later.Scope);
        Assert.Equal(new LockoutScope(1, T0.AddMinutes(16), null), later.ShopWide);
    }

    [Fact]
    public async Task LockoutKeysAreValidated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Store.GetLockoutAsync(LockoutKeys.ShopWide, AddrA, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Store.RecordFailedSignInAsync(LockoutRules.Default, Admin, LockoutKeys.ShopWide, T0, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Store.ResetFailedSignInsAsync(Admin, "", Ct));
    }

    // ---- Items ----

    [Fact]
    public async Task CreateGetAndListItems()
    {
        var item = await AddItemAsync("Blue Shirt", 3, 1500);

        Assert.Equal(new Item(item.Id, "Blue Shirt", 3, 1500, T0, T0), item);
        Assert.Equal(item, await Store.GetItemAsync(item.Id, Ct));
        Assert.Null(await Store.GetItemAsync(Guid.CreateVersion7(), Ct));
        Assert.Equal([item], (await Store.ListItemsAsync(null, 1, 50, Ct)).Items);
    }

    [Fact]
    public async Task ItemNamesAreUniqueIgnoringCase()
    {
        await AddItemAsync("Blue Shirt");

        var duplicate = await Store.CreateItemAsync(Guid.CreateVersion7(), new ItemDraft("BLUE shirt", 1, 100), T0, Ct);

        Assert.Equal(ItemWriteStatus.NameExists, duplicate.Status);
        Assert.Single((await Store.ListItemsAsync(null, 1, 50, Ct)).Items);
    }

    [Fact]
    public async Task UpdateItem()
    {
        var item = await AddItemAsync("Blue Shirt");
        var other = await AddItemAsync("Red Shirt");

        var renamed = await Store.UpdateItemAsync(item.Id, new ItemDraft("Navy Shirt", 7, 2000), T0.AddHours(1), Ct);
        Assert.Equal(new Item(item.Id, "Navy Shirt", 7, 2000, T0, T0.AddHours(1)), renamed.Item);

        var caseOnly = await Store.UpdateItemAsync(item.Id, new ItemDraft("NAVY shirt", 7, 2000), T0, Ct);
        Assert.Equal(ItemWriteStatus.Ok, caseOnly.Status);

        var clash = await Store.UpdateItemAsync(item.Id, new ItemDraft("red SHIRT", 1, 100), T0, Ct);
        Assert.Equal(ItemWriteStatus.NameExists, clash.Status);
        Assert.Equal("NAVY shirt", (await Store.GetItemAsync(item.Id, Ct))!.Name);

        var missing = await Store.UpdateItemAsync(Guid.CreateVersion7(), new ItemDraft("Ghost", 1, 100), T0, Ct);
        Assert.Equal(ItemWriteStatus.NotFound, missing.Status);

        // The old name is free again after the rename; the other item is untouched.
        Assert.Equal(ItemWriteStatus.Ok, (await Store.CreateItemAsync(Guid.CreateVersion7(), new ItemDraft("Blue Shirt", 1, 100), T0, Ct)).Status);
        Assert.Equal(other, await Store.GetItemAsync(other.Id, Ct));
    }

    [Fact]
    public async Task DeleteItemFreesItsName()
    {
        var item = await AddItemAsync("Blue Shirt");

        Assert.True(await Store.DeleteItemAsync(item.Id, Ct));
        Assert.False(await Store.DeleteItemAsync(item.Id, Ct));
        Assert.Null(await Store.GetItemAsync(item.Id, Ct));
        Assert.Equal(ItemWriteStatus.Ok, (await Store.CreateItemAsync(Guid.CreateVersion7(), new ItemDraft("blue shirt", 1, 100), T0, Ct)).Status);
    }

    [Fact]
    public async Task SearchIsCaseInsensitiveSubstringSortedByName()
    {
        await AddItemAsync("Red Shirt");
        await AddItemAsync("blue shirt");
        await AddItemAsync("Shoes");
        await AddItemAsync("Hat");

        var shirts = await Store.ListItemsAsync("SHIRT", 1, 50, Ct);
        Assert.Equal(["blue shirt", "Red Shirt"], shirts.Items.Select(i => i.Name));
        Assert.Equal(2, shirts.Total);

        var all = await Store.ListItemsAsync(null, 1, 50, Ct);
        Assert.Equal(["blue shirt", "Hat", "Red Shirt", "Shoes"], all.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task SearchTreatsWildcardsLiterally()
    {
        await AddItemAsync("50% off");
        await AddItemAsync("500 pieces");
        await AddItemAsync("a_b");
        await AddItemAsync("axb");
        await AddItemAsync(@"back\slash");

        Assert.Equal(["50% off"], (await Store.ListItemsAsync("0%", 1, 50, Ct)).Items.Select(i => i.Name));
        Assert.Equal(["a_b"], (await Store.ListItemsAsync("_", 1, 50, Ct)).Items.Select(i => i.Name));
        Assert.Equal([@"back\slash"], (await Store.ListItemsAsync(@"\", 1, 50, Ct)).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task ItemsArePaged()
    {
        for (var i = 0; i < 7; i++)
        {
            await AddItemAsync($"Item {i}");
        }

        var page2 = await Store.ListItemsAsync("item", 2, 3, Ct);
        var page4 = await Store.ListItemsAsync("item", 4, 3, Ct);

        Assert.Equal(["Item 3", "Item 4", "Item 5"], page2.Items.Select(i => i.Name));
        Assert.Equal(7, page2.Total);
        Assert.Equal(2, page2.PageNumber);
        Assert.Empty(page4.Items);
        Assert.Equal(7, page4.Total);
    }

    // ---- Orders ----

    [Fact]
    public async Task PlaceOrderDecrementsStockAndStoresSnapshot()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1500);
        var hat = await AddItemAsync("Hat", 1, 999);
        var request = Order(new OrderRequestLine(shirt.Id, 2, 1500), new(hat.Id, 1, 999));

        var result = await Store.PlaceOrderAsync(request, Ct);

        Assert.Empty(result.Problems);
        var expected = new Order(request.Reference, T0.AddMinutes(1), OrderStatus.PendingPayment, Wallet, 3999,
            [new(shirt.Id, "Shirt", 1500, 2), new(hat.Id, "Hat", 999, 1)]);
        AssertOrder(expected, result.Order!);
        Assert.Equal(3, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
        Assert.Equal(0, (await Store.GetItemAsync(hat.Id, Ct))!.Stock);
        Assert.False((await Store.GetItemAsync(hat.Id, Ct))!.Available);

        var orders = await Store.ListOrdersAsync(1, 20, Ct);
        Assert.Equal(1, orders.Total);
        AssertOrder(expected, orders.Items.Single());
    }

    [Fact]
    public async Task OrderProblemsChangeNothing()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1500);
        var hat = await AddItemAsync("Hat", 1, 999);
        var gone = Guid.CreateVersion7();

        var result = await Store.PlaceOrderAsync(
            Order(new OrderRequestLine(shirt.Id, 1, 1400), new(hat.Id, 2, 999), new(gone, 1, 100)), Ct);

        Assert.Null(result.Order);
        Assert.Equal(
            [
                new OrderProblem(shirt.Id, ProblemKind.Price, 1500, 5),
                new OrderProblem(hat.Id, ProblemKind.Stock, null, 1),
                new OrderProblem(gone, ProblemKind.Removed),
            ],
            result.Problems.OrderBy(p => p.Kind == ProblemKind.Price ? 0 : p.Kind == ProblemKind.Stock ? 1 : 2));
        Assert.Equal(5, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
        Assert.Equal(1, (await Store.GetItemAsync(hat.Id, Ct))!.Stock);
        Assert.Equal(0, (await Store.ListOrdersAsync(1, 20, Ct)).Total);
    }

    /// <summary>
    /// The client's price is only an expectation (FR-013): a lower or higher price never produces
    /// an order. Stored line prices and the total come from the store.
    /// </summary>
    [Fact]
    public async Task ClientCannotChooseThePrice()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1500);

        foreach (var claimed in new long[] { 1, 1499, 1501, 999_999 })
        {
            var rejected = await Store.PlaceOrderAsync(Order(new OrderRequestLine(shirt.Id, 2, claimed)), Ct);
            Assert.Null(rejected.Order);
            Assert.Equal([new OrderProblem(shirt.Id, ProblemKind.Price, 1500, 5)], rejected.Problems);
        }

        Assert.Equal(0, (await Store.ListOrdersAsync(1, 20, Ct)).Total);
        Assert.Equal(5, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);

        var placed = (await Store.PlaceOrderAsync(Order(new OrderRequestLine(shirt.Id, 2, 1500)), Ct)).Order!;
        var stored = (await Store.ListOrdersAsync(1, 20, Ct)).Items.Single();
        Assert.All(new[] { placed, stored }, o =>
        {
            Assert.Equal(1500, o.Lines.Single().UnitPriceCents);
            Assert.Equal(3000, o.TotalCents);
        });
    }

    [Fact]
    public async Task LargeTotalsStayExact()
    {
        var item = await AddItemAsync("Gold bar", 100_000, Money.MaxPriceCents);

        var placed = (await Store.PlaceOrderAsync(
            Order(new OrderRequestLine(item.Id, 90_071, Money.MaxPriceCents)), Ct)).Order!;

        Assert.Equal(9_007_099_999_909_929, placed.TotalCents);
        Assert.Equal(9_007_099_999_909_929, (await Store.ListOrdersAsync(1, 20, Ct)).Items.Single().TotalCents);
        Assert.Equal(100_000 - 90_071, (await Store.GetItemAsync(item.Id, Ct))!.Stock);
    }

    [Fact]
    public async Task ReferenceCollisionIsReportedAndChangesNothing()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1500);
        var first = Order(new OrderRequestLine(shirt.Id, 1, 1500));
        await Store.PlaceOrderAsync(first, Ct);

        var again = await Store.PlaceOrderAsync(first with { Lines = [new(shirt.Id, 2, 1500)] }, Ct);

        Assert.True(again.ReferenceTaken);
        Assert.Null(again.Order);
        Assert.Equal(4, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
        Assert.Equal(1, (await Store.ListOrdersAsync(1, 20, Ct)).Total);
    }

    /// <summary>SC-004: 20 concurrent orders for an item with 5 in stock → exactly 5 succeed.</summary>
    [Fact]
    public async Task ConcurrentOrdersNeverOversell()
    {
        var item = await AddItemAsync("Limited", 5, 1000);
        var other = await AddItemAsync("Filler", 1000, 100);
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            replicas[i % 3].PlaceOrderAsync(Order(new OrderRequestLine(other.Id, 1, 100), new(item.Id, 1, 1000)), Ct), Ct)));

        Assert.Equal(5, results.Count(r => r.Order is not null));
        Assert.All(results.Where(r => r.Order is null), r =>
            Assert.Equal([new OrderProblem(item.Id, ProblemKind.Stock, null, 0)], r.Problems));
        Assert.Equal(0, (await Store.GetItemAsync(item.Id, Ct))!.Stock);
        Assert.Equal(995, (await Store.GetItemAsync(other.Id, Ct))!.Stock);
        Assert.Equal(5, (await Store.ListOrdersAsync(1, 20, Ct)).Total);
    }

    [Fact]
    public async Task OrderKeepsItsSnapshotAfterRenameAndDelete()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1500);
        var hat = await AddItemAsync("Hat", 5, 999);
        var placed = (await Store.PlaceOrderAsync(Order(new OrderRequestLine(shirt.Id, 1, 1500), new(hat.Id, 1, 999)), Ct)).Order!;

        await Store.UpdateItemAsync(shirt.Id, new ItemDraft("Renamed", 1, 1), T0, Ct);
        await Store.DeleteItemAsync(hat.Id, Ct);

        AssertOrder(placed, (await Store.ListOrdersAsync(1, 20, Ct)).Items.Single());
    }

    [Fact]
    public async Task OrdersAreListedNewestFirstAndPaged()
    {
        var item = await AddItemAsync("Shirt", 100, 100);
        var references = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var order = Order(new OrderRequestLine(item.Id, 1, 100)) with { CreatedAt = T0.AddMinutes(i) };
            references.Add(order.Reference);
            await Store.PlaceOrderAsync(order, Ct);
        }

        var page1 = await Store.ListOrdersAsync(1, 2, Ct);
        var page3 = await Store.ListOrdersAsync(3, 2, Ct);

        Assert.Equal(5, page1.Total);
        Assert.Equal([references[4], references[3]], page1.Items.Select(o => o.Reference));
        Assert.Equal([references[0]], page3.Items.Select(o => o.Reference));
        Assert.Empty((await Store.ListOrdersAsync(4, 2, Ct)).Items);
    }

    // ---- Data-Protection keys and health ----

    [Fact]
    public async Task DataProtectionKeysAreStoredAndReplaced()
    {
        Assert.Empty(await Store.GetDataProtectionKeysAsync(Ct));

        await Store.StoreDataProtectionKeyAsync("key-b", "<key id=\"b\" />", Ct);
        await Store.StoreDataProtectionKeyAsync("key-a", "<key id=\"a\" />", Ct);
        await Store.StoreDataProtectionKeyAsync("key-a", "<key id=\"a2\" />", Ct);

        Assert.Equal(
            [new DataProtectionKey("key-a", "<key id=\"a2\" />"), new DataProtectionKey("key-b", "<key id=\"b\" />")],
            await NewStore().GetDataProtectionKeysAsync(Ct));
    }

    [Fact]
    public async Task PingSucceeds() => await Store.PingAsync(Ct);

    private static void AssertOrder(Order expected, Order actual)
    {
        Assert.Equal(expected with { Lines = [] }, actual with { Lines = [] });
        Assert.Equal(expected.Lines, actual.Lines);
    }
}
