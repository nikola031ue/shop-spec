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
        existing ?? new AdminRecord("hash-initial", true, null, 0, null, null);

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
        Assert.Equal(new AdminRecord("hash-initial", true, null, 0, null, null), admin);

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
            return new AdminRecord("hash-" + Guid.NewGuid(), true, null, 0, null, null);
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
        await Store.SetAdminPasswordAsync("hash-changed", T0, Ct);

        var admin = await Store.GetAdminAsync(Ct);
        Assert.Equal("hash-changed", admin!.PasswordHash);
        Assert.False(admin.Initial);
        Assert.Equal(T0, admin.ChangedAt);
    }

    [Fact]
    public async Task LockoutCountsFailuresAndLocksOnTheFifth()
    {
        var policy = LockoutPolicy.Default;
        for (var i = 1; i <= 4; i++)
        {
            var admin = await Store.RecordFailedSignInAsync(policy, T0.AddMinutes(i), Ct);
            Assert.Equal(i, admin.FailedCount);
            Assert.Equal(T0.AddMinutes(1), admin.FailedWindowStart);
            Assert.Null(admin.LockedUntil);
        }

        var locked = await Store.RecordFailedSignInAsync(policy, T0.AddMinutes(5), Ct);

        Assert.Equal(T0.AddMinutes(20), locked.LockedUntil);
        Assert.Equal(0, locked.FailedCount);
        Assert.Null(locked.FailedWindowStart);
        Assert.Equal(locked, await Store.GetAdminAsync(Ct));

        // Failures while locked don't change anything.
        Assert.Equal(locked, await Store.RecordFailedSignInAsync(policy, T0.AddMinutes(6), Ct));
    }

    [Fact]
    public async Task LockoutWindowStartsAtTheFirstFailure()
    {
        var policy = LockoutPolicy.Default;
        await Store.RecordFailedSignInAsync(policy, T0, Ct);
        await Store.RecordFailedSignInAsync(policy, T0.AddMinutes(14), Ct);

        var admin = await Store.RecordFailedSignInAsync(policy, T0.AddMinutes(15), Ct);

        Assert.Equal(1, admin.FailedCount);
        Assert.Equal(T0.AddMinutes(15), admin.FailedWindowStart);
    }

    [Fact]
    public async Task ConcurrentFailuresAreAllCounted()
    {
        var policy = new LockoutPolicy(100, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i =>
            Task.Run(() => replicas[i % 3].RecordFailedSignInAsync(policy, T0, Ct), Ct)));

        Assert.Equal(30, (await Store.GetAdminAsync(Ct))!.FailedCount);
    }

    [Fact]
    public async Task ResetClearsFailuresAndLock()
    {
        var policy = new LockoutPolicy(1, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        await Store.RecordFailedSignInAsync(policy, T0, Ct);

        await Store.ResetFailedSignInsAsync(Ct);

        var admin = await Store.GetAdminAsync(Ct);
        Assert.Equal(0, admin!.FailedCount);
        Assert.Null(admin.FailedWindowStart);
        Assert.Null(admin.LockedUntil);
        Assert.Equal("hash-initial", admin.PasswordHash);
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
