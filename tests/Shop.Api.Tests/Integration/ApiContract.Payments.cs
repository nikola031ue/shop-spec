using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shop.Api.Domain;
using Shop.Api.Payments;
using Shop.Api.Storage;

namespace Shop.Api.Tests.Integration;

public sealed record PaymentsJson(string? TokenAddress, long ChainId, string ExplorerTxUrl, string MintPerCall);

public sealed record ShopWithPaymentsJson(string Name, bool AdminPasswordChanged, PaymentsJson Payments);

public sealed record PublicPaymentJson(string Tx, long Block, DateTimeOffset PaidAt, string ExplorerUrl);

public sealed record PublicOrderJson(
    string Reference, string Status, long TotalCents, string TotalBaseUnits, string WalletAddress, string? PayingAccount,
    DateTimeOffset CreatedAt, DateTimeOffset Deadline, PublicPaymentJson? Payment);

public sealed record AdminPaymentJson(
    string Tx, int LogIndex, long Block, DateTimeOffset BlockTime, string From, string To, string ValueBaseUnits,
    string? OrderReference, string? Reason, string ExplorerUrl);

public sealed record AdminPaymentPageJson(List<AdminPaymentJson> Payments, int Page, int PageSize, long Total);

/// <summary>
/// Spec 005 through HTTP, once per store: checkout with a paying account, the order page, the admin
/// payments list (TestServer); and the operator's internal API on its own Kestrel listener with
/// real ports (contracts/shop-api.md, contracts/shop-internal-api.md).
/// </summary>
public abstract partial class ApiContract
{
    private const string InternalToken = "test-internal-token-0123456789abcdefghijklmn";
    private const string Wallet = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";

    private static object Line(ItemJson item, int quantity = 1) =>
        new { itemId = item.Id, quantity, unitPriceCents = item.PriceCents };

    private static async Task<HttpResponseMessage> CheckoutAsync(HttpClient client, string? payingAccount, params object[] lines) =>
        await client.PostAsJsonAsync(new Uri("/api/orders", UriKind.Relative), new { lines, payingAccount }, Ct);

    // ---- Checkout and order page (TestServer) ----

    [Fact]
    public async Task ShopDescribesPayments()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var anonymous = app.CreateClient();

        var shop = (await anonymous.GetFromJsonAsync<ShopWithPaymentsJson>("/api/shop", Ct))!;

        Assert.Equal(new PaymentsJson(TestAccounts.Token, 11155111, "https://sepolia.etherscan.io/tx/", "1000000000"), shop.Payments);
    }

    [Fact]
    public async Task CheckoutNeedsAPayingAccountAndConfiguredPayments()
    {
        var env = await FreshEnvAsync();
        await using (var app = new ShopApp(env))
        {
            using var admin = await SignedInAsync(app);
            var shirt = await CreateAsync(admin, "Shirt", 5, "11.00");
            using var anonymous = app.CreateClient();

            foreach (var account in new[] { null, "", "0x123", "0x5AAeb6053F3E94C9b9A09f33669435E7Ef1BeAed" })
            {
                using var response = await CheckoutAsync(anonymous, account, Line(shirt));
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty("payingAccount", out _));
            }
        }

        env.Remove("TOKEN_ADDRESS");
        await using var unpaid = new ShopApp(env);
        using var admin2 = await SignedInAsync(unpaid);
        var cap = await CreateAsync(admin2, "Cap", 5, "5");
        using var client = unpaid.CreateClient();
        using var refused = await CheckoutAsync(client, TestAccounts.New(), Line(cap));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("paymentsUnavailable", (await ProblemAsync(refused)).GetProperty("reason").GetString());
        Assert.Equal(5, (await client.GetFromJsonAsync<ItemPageJson>("/api/items", Ct))!.Items.Single(i => i.Name == "Cap").Stock);
        Assert.Null((await client.GetFromJsonAsync<ShopWithPaymentsJson>("/api/shop", Ct))!.Payments.TokenAddress);
    }

    [Fact]
    public async Task TheOrderPageShowsWhatToPayAndASecondCheckoutIsRefused()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        var shirt = await CreateAsync(admin, "Shirt", 5, "11.00");
        using var anonymous = app.CreateClient();
        var account = TestAccounts.New();

        using var placedResponse = await CheckoutAsync(anonymous, account.ToLowerInvariant(), Line(shirt, 2));
        Assert.Equal(HttpStatusCode.Created, placedResponse.StatusCode);
        var placed = (await placedResponse.Content.ReadFromJsonAsync<PublicOrderJson>(Ct))!;
        Assert.Equal(account, placed.PayingAccount); // stored in EIP-55 form
        Assert.Equal(("PendingPayment", 2200L, "22000000", Wallet), (placed.Status, placed.TotalCents, placed.TotalBaseUnits, placed.WalletAddress));
        Assert.Equal(placed.CreatedAt.AddMinutes(30), placed.Deadline);
        Assert.Null(placed.Payment);

        Assert.Equal(placed, await anonymous.GetFromJsonAsync<PublicOrderJson>($"/api/orders/{placed.Reference}", Ct));

        using var second = await CheckoutAsync(anonymous, account, Line(shirt));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var problem = await ProblemAsync(second);
        Assert.Equal(("waitingOrder", placed.Reference), (problem.GetProperty("reason").GetString(), problem.GetProperty("reference").GetString()));
        Assert.Equal(3, (await anonymous.GetFromJsonAsync<ItemPageJson>("/api/items", Ct))!.Items.Single().Stock);

        // Unknown, too short, a character outside the alphabet (U), wrong case.
        foreach (var reference in new[] { "0000000000", "short", "ABCDEFGHJU", placed.Reference.ToLowerInvariant() })
        {
            using var missing = await anonymous.GetAsync(new Uri($"/api/orders/{reference}", UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Fact]
    public async Task AdminSeesPaymentStatusAndUnmatchedTransfers()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        var shirt = await CreateAsync(admin, "Shirt", 5, "11.00");
        using var anonymous = app.CreateClient();
        using var placedResponse = await CheckoutAsync(anonymous, TestAccounts.New(), Line(shirt));
        var placed = (await placedResponse.Content.ReadFromJsonAsync<PublicOrderJson>(Ct))!;
        var store = app.Services.GetRequiredService<IShopStore>();
        var paidTx = "0x" + new string('a', 64);
        var wrongTx = "0x" + new string('b', 64);
        var blockTime = placed.CreatedAt.AddMinutes(2);
        await store.RecordTransferAsync(new ChainTransfer(paidTx, 1, 7000004, blockTime, placed.PayingAccount!, Wallet, 11_000_000), blockTime, Ct);
        await store.RecordTransferAsync(new ChainTransfer(wrongTx, 0, 7000005, blockTime, TestAccounts.New(), Wallet, 5), blockTime, Ct);

        var orders = await admin.GetJsonAsync<JsonElement>("/api/admin/orders");
        var order = orders.GetProperty("orders")[0];
        Assert.Equal("Paid", order.GetProperty("status").GetString());
        Assert.Equal("https://sepolia.etherscan.io/tx/" + paidTx, order.GetProperty("payment").GetProperty("explorerUrl").GetString());
        Assert.Equal(placed.PayingAccount, order.GetProperty("payingAccount").GetString());

        var unmatched = await admin.GetJsonAsync<AdminPaymentPageJson>("/api/admin/payments?matched=false");
        var only = Assert.Single(unmatched.Payments);
        Assert.Equal((wrongTx, "account", "5", (string?)null), (only.Tx, only.Reason, only.ValueBaseUnits, only.OrderReference));
        Assert.Equal(2, (await admin.GetJsonAsync<AdminPaymentPageJson>("/api/admin/payments")).Total);

        using var anonymousList = await anonymous.GetAsync(new Uri("/api/admin/payments", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousList.StatusCode);
        Assert.Equal("Paid", (await anonymous.GetFromJsonAsync<PublicOrderJson>($"/api/orders/{placed.Reference}", Ct))!.Status);
    }

    [Fact]
    public async Task TheExpiryJobExpiresBehindTheChain()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        var shirt = await CreateAsync(admin, "Shirt", 5, "11.00");
        using var anonymous = app.CreateClient();
        using var placedResponse = await CheckoutAsync(anonymous, TestAccounts.New(), Line(shirt, 3));
        var placed = (await placedResponse.Content.ReadFromJsonAsync<PublicOrderJson>(Ct))!;
        var store = app.Services.GetRequiredService<IShopStore>();
        var clock = new FakeTimeProvider(placed.Deadline.AddMinutes(1));
        var job = new ExpiryJob(store, clock, NullLogger<ExpiryJob>.Instance);

        Assert.Equal(0, await job.RunOnceAsync(Ct)); // past the deadline, but the chain wasn't read that far
        await store.AdvanceChainProgressAsync(new ChainProgress(Wallet, 9, placed.Deadline.AddSeconds(12)), Ct);
        Assert.Equal(1, await job.RunOnceAsync(Ct));

        Assert.Equal("Expired", (await anonymous.GetFromJsonAsync<PublicOrderJson>($"/api/orders/{placed.Reference}", Ct))!.Status);
        Assert.Equal(5, (await anonymous.GetFromJsonAsync<ItemPageJson>("/api/items", Ct))!.Items.Single().Stock);
    }

    // ---- Internal API on its own listener (Kestrel, real ports) ----

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>An app on real Kestrel listeners: (public port, internal port).</summary>
    private async Task<(ShopApp App, HttpClient Public, HttpClient Internal)> KestrelAppAsync()
    {
        var (publicPort, internalPort) = (FreePort(), FreePort());
        var env = await FreshEnvAsync();
        env["INTERNAL_TOKEN"] = InternalToken;
        env["HTTP_PORTS"] = publicPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["INTERNAL_PORT"] = internalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var app = new ShopApp(env);
        app.UseKestrel();
        app.StartServer();
        return (app,
            new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{publicPort}") },
            new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{internalPort}") });
    }

    private static HttpRequestMessage Internal(HttpMethod method, string path, object? body = null, string? token = InternalToken)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static object Heartbeat(string address, long block, DateTimeOffset time, params object[] transfers) =>
        new { address, fromBlock = 1, seenThrough = new { block, time }, transfers };

    [Fact]
    public async Task TheInternalApiIsOnlyOnItsPortAndNeedsTheToken()
    {
        var (app, pub, internalClient) = await KestrelAppAsync();
        await using var _app = app;
        using var _pub = pub;
        using var _int = internalClient;
        var beat = Heartbeat(Wallet, 10, DateTimeOffset.UtcNow);

        // Public port: no internal API, even with the token or a forged Host header.
        using (var r = await pub.SendAsync(Internal(HttpMethod.Post, "/internal/chain", beat), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        using (var forged = Internal(HttpMethod.Post, "/internal/chain", beat))
        {
            forged.Headers.Host = internalClient.BaseAddress!.Authority;
            using var r = await pub.SendAsync(forged, Ct);
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        using (var r = await pub.GetAsync(new Uri("/internal/anything", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await pub.GetAsync(new Uri("/api/shop", UriKind.Relative), Ct)).StatusCode);

        // Internal port: nothing but /internal, and only with the right token.
        Assert.Equal(HttpStatusCode.NotFound, (await internalClient.GetAsync(new Uri("/api/shop", UriKind.Relative), Ct)).StatusCode);
        foreach (var token in new[] { null, "wrong", InternalToken + "x", InternalToken[..^1] })
        {
            using var r = await internalClient.SendAsync(Internal(HttpMethod.Post, "/internal/chain", beat, token), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        using (var r = await internalClient.SendAsync(Internal(HttpMethod.Post, "/internal/chain", beat), Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        using (var r = await internalClient.SendAsync(Internal(HttpMethod.Post, "/internal/chain",
                   Heartbeat("0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359", 10, DateTimeOffset.UtcNow)), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); // not one of this shop's addresses
        }

        Assert.Equal(10, (await app.Services.GetRequiredService<IShopStore>().GetChainProgressAsync(Wallet, Ct))!.SeenThroughBlock);
    }

    [Fact]
    public async Task DeliveredTransfersPayOrdersAndAreRecordedOnce()
    {
        var (app, pub, internalClient) = await KestrelAppAsync();
        await using var _app = app;
        using var _pub = pub;
        using var _int = internalClient;
        var store = app.Services.GetRequiredService<IShopStore>();
        var shirt = (await store.CreateItemAsync(Guid.CreateVersion7(), new ItemDraft("Shirt", 5, 1100), DateTimeOffset.UtcNow, Ct)).Item!;
        var account = TestAccounts.New();
        using var placedResponse = await CheckoutAsync(pub, account, new { itemId = shirt.Id, quantity = 1, unitPriceCents = 1100 });
        var placed = (await placedResponse.Content.ReadFromJsonAsync<PublicOrderJson>(Ct))!;
        var blockTime = placed.CreatedAt.AddSeconds(30);
        var paying = new { tx = "0x" + new string('c', 64), logIndex = 2, block = 7000004, blockTime, from = account.ToLowerInvariant(), to = Wallet.ToLowerInvariant(), value = "11000000" };
        var wrong = new { tx = "0x" + new string('d', 64), logIndex = 0, block = 7000003, blockTime, from = account, to = Wallet, value = "10999999" };

        using (var r = await internalClient.SendAsync(Internal(HttpMethod.Get, $"/internal/wallets/{Wallet}/waiting"), Ct))
        {
            Assert.Equal(1, (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("waiting").GetInt64());
        }

        for (var delivery = 0; delivery < 2; delivery++) // a redelivery of the same range changes nothing
        {
            using var r = await internalClient.SendAsync(Internal(HttpMethod.Post, "/internal/chain",
                Heartbeat(Wallet, 7000010, blockTime.AddMinutes(1), paying, wrong)), Ct);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        var order = (await pub.GetFromJsonAsync<PublicOrderJson>($"/api/orders/{placed.Reference}", Ct))!;
        Assert.Equal("Paid", order.Status);
        Assert.Equal(new PublicPaymentJson(paying.tx, 7000004, blockTime, "https://sepolia.etherscan.io/tx/" + paying.tx), order.Payment);
        var payments = await store.ListPaymentsAsync(false, 1, 20, Ct);
        Assert.Equal(2, payments.Total);
        Assert.Equal(UnmatchedReason.Amount, Assert.Single((await store.ListPaymentsAsync(true, 1, 20, Ct)).Items).UnmatchedReason);

        using (var bad = await internalClient.SendAsync(Internal(HttpMethod.Post, "/internal/chain",
                   Heartbeat(Wallet, 7000010, blockTime, paying with { to = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359" })), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); // a transfer to another address
        }

        using (var bad = await internalClient.SendAsync(Internal(HttpMethod.Post, "/internal/chain",
                   Heartbeat(Wallet, 7000000, blockTime, paying)), Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode); // a transfer past seenThrough
        }

        using (var r = await internalClient.SendAsync(Internal(HttpMethod.Get, $"/internal/wallets/{Wallet.ToLowerInvariant()}/waiting"), Ct))
        {
            Assert.Equal(0, (await r.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("waiting").GetInt64());
        }
    }
}
