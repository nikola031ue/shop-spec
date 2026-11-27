using System.Numerics;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Tests.Integration;

/// <summary>
/// Spec 005 on both stores (data-model.md "Shop app"; research R5, R6): one waiting order per
/// account, matching and its reasons, idempotent receipt, chain progress, expiry exactly once.
/// Orders are placed at T0+1 min with a deadline at T0+31 min.
/// </summary>
public abstract partial class ShopStoreContract
{
    private static readonly DateTimeOffset Placed = T0.AddMinutes(1);
    private static readonly DateTimeOffset Deadline = T0.AddMinutes(31);
    private const string OtherWallet = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";
    private static int _txCounter;

    private static string NewTx() =>
        "0x" + Interlocked.Increment(ref _txCounter).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);

    private static ChainTransfer Transfer(
        Order order, BigInteger? value = null, DateTimeOffset? blockTime = null, string? from = null, string? to = null,
        string? tx = null, int logIndex = 0) =>
        new(tx ?? NewTx(), logIndex, 100, blockTime ?? Placed.AddMinutes(5), from ?? order.PayingAccount!,
            to ?? order.WalletAddress, value ?? order.TotalBaseUnits);

    private async Task<Order> PlaceAsync(Item item, int quantity = 1, string? account = null)
    {
        var result = await Store.PlaceOrderAsync(
            Order(new OrderRequestLine(item.Id, quantity, item.PriceCents)) with { PayingAccount = account ?? TestAccounts.New() },
            Ct);
        Assert.NotNull(result.Order);
        return result.Order;
    }

    private Task ProgressAsync(DateTimeOffset seenThrough, long block = 200, string address = Wallet) =>
        Store.AdvanceChainProgressAsync(new ChainProgress(address, block, seenThrough), Ct);

    private async Task<Payment> OnlyPaymentAsync(bool unmatchedOnly = false) =>
        Assert.Single((await Store.ListPaymentsAsync(unmatchedOnly, 1, 20, Ct)).Items);

    // ---- Checkout (FR-002a, FR-005a) ----

    [Fact]
    public async Task OrdersStoreThePayingAccountDeadlineAndBaseUnits()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var account = TestAccounts.New();

        var order = await PlaceAsync(shirt, 2, account);

        var stored = (await Store.GetOrderAsync(order.Reference, Ct))!;
        Assert.Equal(account, stored.PayingAccount);
        Assert.Equal(Deadline, stored.Deadline);
        Assert.Equal(OrderStatus.PendingPayment, stored.Status);
        Assert.Equal(new BigInteger(22_000_000), stored.TotalBaseUnits); // 22.00 mUSDT, 6 decimals
        Assert.Null(stored.Payment);
        Assert.Null(await Store.GetOrderAsync("0000000000", Ct));
    }

    [Fact]
    public async Task AnAccountHasAtMostOneWaitingOrder()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var account = TestAccounts.New();
        var first = await PlaceAsync(shirt, 1, account);

        var second = await Store.PlaceOrderAsync(
            Order(new OrderRequestLine(shirt.Id, 1, 1100)) with { PayingAccount = account }, Ct);

        Assert.Equal(first.Reference, second.WaitingOrder);
        Assert.Null(second.Order);
        Assert.Equal(4, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
        Assert.Equal(1, (await Store.ListOrdersAsync(1, 20, Ct)).Total);

        // Another account may order; once the first is paid, the account may order again.
        await PlaceAsync(shirt);
        Assert.Equal(TransferOutcome.Paid, await Store.RecordTransferAsync(Transfer(first), T0, Ct));
        await PlaceAsync(shirt, 1, account);
    }

    [Fact]
    public async Task ConcurrentCheckoutsWithOneAccountPlaceOneOrder()
    {
        var item = await AddItemAsync("Shirt", 100, 1000);
        var account = TestAccounts.New();
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            replicas[i % 3].PlaceOrderAsync(
                Order(new OrderRequestLine(item.Id, 1, 1000)) with { PayingAccount = account }, Ct))));

        var placed = Assert.Single(results, r => r.Order is not null);
        Assert.All(results.Where(r => r.Order is null), r => Assert.Equal(placed.Order!.Reference, r.WaitingOrder));
        Assert.Equal(99, (await Store.GetItemAsync(item.Id, Ct))!.Stock);
    }

    // ---- Matching (research R6) ----

    [Fact]
    public async Task AnExactTransferPaysItsOrderOnce()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var order = await PlaceAsync(shirt);
        var transfer = Transfer(order, tx: NewTx(), logIndex: 3);

        Assert.Equal(TransferOutcome.Paid, await Store.RecordTransferAsync(transfer, T0.AddMinutes(10), Ct));
        Assert.Equal(TransferOutcome.AlreadyRecorded, await Store.RecordTransferAsync(transfer, T0.AddMinutes(11), Ct));

        var paid = (await Store.GetOrderAsync(order.Reference, Ct))!;
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Equal(new OrderPayment(transfer.Tx, 3, 100, transfer.BlockTime), paid.Payment);
        var payment = await OnlyPaymentAsync();
        Assert.Equal(new Payment(transfer.Tx, 3, 100, transfer.BlockTime, order.PayingAccount!, Wallet,
            order.TotalBaseUnits, order.Reference, null, T0.AddMinutes(10)), payment);
        Assert.Empty((await Store.ListPaymentsAsync(true, 1, 20, Ct)).Items);
        Assert.Equal(4, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
    }

    [Fact]
    public async Task WrongTransfersAreRecordedWithTheirReason()
    {
        var shirt = await AddItemAsync("Shirt", 50, 1100);
        var waiting = await PlaceAsync(shirt);
        var paidOrder = await PlaceAsync(shirt);
        Assert.Equal(TransferOutcome.Paid, await Store.RecordTransferAsync(Transfer(paidOrder), T0, Ct));

        var cases = new (ChainTransfer Transfer, string Reason)[]
        {
            (Transfer(waiting, value: waiting.TotalBaseUnits + 10_000), UnmatchedReason.Amount),
            (Transfer(waiting, from: TestAccounts.New()), UnmatchedReason.Account),
            (Transfer(waiting, blockTime: Deadline.AddSeconds(1)), UnmatchedReason.Late),
            (Transfer(waiting, blockTime: Placed.AddSeconds(-1)), UnmatchedReason.Unknown),
            (Transfer(waiting, to: OtherWallet), UnmatchedReason.Unknown),
            (Transfer(paidOrder), UnmatchedReason.Duplicate),
        };
        foreach (var (transfer, _) in cases)
        {
            Assert.Equal(TransferOutcome.Unmatched, await Store.RecordTransferAsync(transfer, T0, Ct));
        }

        var unmatched = (await Store.ListPaymentsAsync(true, 1, 20, Ct)).Items;
        Assert.Equal(cases.Length, unmatched.Count);
        foreach (var (transfer, reason) in cases)
        {
            var p = Assert.Single(unmatched, p => p.Tx == transfer.Tx);
            Assert.Equal(reason, p.UnmatchedReason);
            Assert.Null(p.OrderReference);
        }

        Assert.Equal(OrderStatus.PendingPayment, (await Store.GetOrderAsync(waiting.Reference, Ct))!.Status);
        Assert.Equal(cases.Length + 1, (await Store.ListPaymentsAsync(false, 1, 20, Ct)).Total);
    }

    [Fact]
    public async Task ConcurrentDeliveriesPayOnce()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var order = await PlaceAsync(shirt);
        var same = Transfer(order);
        var second = Transfer(order); // another tx with the same amount from the same account
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => Task.Run(() => replicas[i % 3].RecordTransferAsync(same, T0, Ct)))
                .Append(Task.Run(() => replicas[0].RecordTransferAsync(second, T0, Ct))));

        Assert.Single(outcomes, o => o == TransferOutcome.Paid);
        Assert.Single(outcomes, o => o == TransferOutcome.Unmatched); // the other tx: duplicate
        Assert.Equal(5, outcomes.Count(o => o == TransferOutcome.AlreadyRecorded));
        Assert.Equal(OrderStatus.Paid, (await Store.GetOrderAsync(order.Reference, Ct))!.Status);
        Assert.Equal(UnmatchedReason.Duplicate, (await OnlyPaymentAsync(unmatchedOnly: true)).UnmatchedReason);
    }

    // ---- Chain progress ----

    [Fact]
    public async Task ChainProgressOnlyMovesForward()
    {
        Assert.False(await Store.IsKnownAddressAsync(OtherWallet, Ct));
        Assert.Null(await Store.GetChainProgressAsync(OtherWallet, Ct));

        await ProgressAsync(T0.AddMinutes(10), 500, OtherWallet);
        await ProgressAsync(T0.AddMinutes(5), 400, OtherWallet);

        Assert.Equal(new ChainProgress(OtherWallet, 500, T0.AddMinutes(10)), await Store.GetChainProgressAsync(OtherWallet, Ct));
        Assert.True(await Store.IsKnownAddressAsync(OtherWallet, Ct));
    }

    [Fact]
    public async Task OrderAddressesAreKnownAndCounted()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        Assert.False(await Store.IsKnownAddressAsync(Wallet, Ct));
        var first = await PlaceAsync(shirt);
        await PlaceAsync(shirt);

        Assert.True(await Store.IsKnownAddressAsync(Wallet, Ct));
        Assert.Equal(2, await Store.CountWaitingAsync(Wallet, Ct));
        Assert.Equal(0, await Store.CountWaitingAsync(OtherWallet, Ct));

        await Store.RecordTransferAsync(Transfer(first), T0, Ct);
        Assert.Equal(1, await Store.CountWaitingAsync(Wallet, Ct));
    }

    // ---- Expiry (FR-009, FR-011, research R5) ----

    [Fact]
    public async Task OrdersExpireOnlyOnceTheChainIsReadPastTheirDeadline()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var order = await PlaceAsync(shirt, 2);
        var late = Deadline.AddMinutes(1);

        Assert.Equal(0, await Store.ExpireDueOrdersAsync(late, 100, Ct)); // no progress at all
        await ProgressAsync(Deadline.AddSeconds(-1));
        Assert.Equal(0, await Store.ExpireDueOrdersAsync(late, 100, Ct)); // chain read only up to the deadline
        await ProgressAsync(Deadline.AddSeconds(30), 300);
        Assert.Equal(0, await Store.ExpireDueOrdersAsync(Deadline.AddSeconds(-1), 100, Ct)); // not due yet

        Assert.Equal(1, await Store.ExpireDueOrdersAsync(late, 100, Ct));

        Assert.Equal(OrderStatus.Expired, (await Store.GetOrderAsync(order.Reference, Ct))!.Status);
        Assert.Equal(5, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
        Assert.Equal(0, await Store.CountWaitingAsync(Wallet, Ct));
        Assert.Equal(0, await Store.ExpireDueOrdersAsync(late.AddMinutes(1), 100, Ct));

        // The account may order again.
        await PlaceAsync(shirt, 1, order.PayingAccount);
    }

    [Fact]
    public async Task ConcurrentExpiryReturnsStockOnce()
    {
        var shirt = await AddItemAsync("Shirt", 30, 1100);
        for (var i = 0; i < 10; i++)
        {
            await PlaceAsync(shirt, 2);
        }

        var paid = await PlaceAsync(shirt, 2);
        await Store.RecordTransferAsync(Transfer(paid), T0, Ct);
        await ProgressAsync(Deadline.AddMinutes(5));
        var replicas = Enumerable.Range(0, 3).Select(_ => NewStore()).ToList();

        var counts = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            var total = 0;
            int n;
            do
            {
                n = await replicas[i % 3].ExpireDueOrdersAsync(Deadline.AddMinutes(10), 3, Ct);
                total += n;
            }
            while (n > 0);

            return total;
        })));

        Assert.Equal(10, counts.Sum());
        Assert.Equal(28, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock); // 30 − 2 for the paid order
        Assert.Equal(OrderStatus.Paid, (await Store.GetOrderAsync(paid.Reference, Ct))!.Status);
    }

    [Fact]
    public async Task ExpiryGivesNothingBackForADeletedItem()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var hat = await AddItemAsync("Hat", 5, 500);
        var result = await Store.PlaceOrderAsync(
            Order(new OrderRequestLine(shirt.Id, 1, 1100), new(hat.Id, 2, 500)) with { PayingAccount = TestAccounts.New() }, Ct);
        Assert.True(await Store.DeleteItemAsync(hat.Id, Ct));
        await ProgressAsync(Deadline.AddMinutes(1));

        Assert.Equal(1, await Store.ExpireDueOrdersAsync(Deadline.AddMinutes(2), 100, Ct));

        Assert.Equal(5, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
        Assert.Null(await Store.GetItemAsync(hat.Id, Ct));
        Assert.Equal(OrderStatus.Expired, (await Store.GetOrderAsync(result.Order!.Reference, Ct))!.Status);
    }

    [Fact]
    public async Task APaymentAfterExpiryIsRecordedLateAndChangesNothing()
    {
        var shirt = await AddItemAsync("Shirt", 5, 1100);
        var order = await PlaceAsync(shirt, 2);
        await ProgressAsync(Deadline.AddMinutes(1));
        await Store.ExpireDueOrdersAsync(Deadline.AddMinutes(2), 100, Ct);

        var outcome = await Store.RecordTransferAsync(Transfer(order, blockTime: Deadline.AddMinutes(3)), T0, Ct);

        Assert.Equal(TransferOutcome.Unmatched, outcome);
        Assert.Equal(UnmatchedReason.Late, (await OnlyPaymentAsync(unmatchedOnly: true)).UnmatchedReason);
        Assert.Equal(OrderStatus.Expired, (await Store.GetOrderAsync(order.Reference, Ct))!.Status);
        Assert.Equal(5, (await Store.GetItemAsync(shirt.Id, Ct))!.Stock);
    }
}
