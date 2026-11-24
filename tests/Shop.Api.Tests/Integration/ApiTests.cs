using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shop.Api.Domain;
using Shop.Api.Storage;
using StackExchange.Redis;

namespace Shop.Api.Tests.Integration;

/// <summary>A browser-like admin client: cookies plus the XSRF header taken from the XSRF-TOKEN cookie.</summary>
public sealed class AdminClient(HttpClient http) : IDisposable
{
    public HttpClient Http { get; } = http;

    public string? Xsrf { get; set; }

    public string? SessionCookie { get; private set; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<HttpResponseMessage> LoginAsync(string password) =>
        await SendAsync(HttpMethod.Post, "/api/admin/login", new { password });

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool xsrf = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (xsrf && Xsrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", Xsrf);
        }

        var response = await Http.SendAsync(request, Ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                var pair = cookie.Split(';')[0].Split('=', 2);
                if (pair[0] == "XSRF-TOKEN")
                {
                    Xsrf = Uri.UnescapeDataString(pair[1]);
                }
                else if (pair[0] == "shop.session" && pair[1].Length > 0)
                {
                    SessionCookie = cookie.Split(';')[0];
                }
            }
        }

        return response;
    }

    public async Task<T> GetJsonAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    public void Dispose() => Http.Dispose();
}

public sealed record ItemJson(Guid Id, string Name, long PriceCents, int Stock, bool Available);

public sealed record ItemPageJson(List<ItemJson> Items, int Page, int PageSize, long Total);

public sealed record MeJson(bool InitialPassword);

public sealed record PlacedJson(string Reference, string Status, long TotalCents, DateTimeOffset CreatedAt);

/// <summary>
/// US1 scenarios 1–6 and the admin security requirements through HTTP, once per store
/// (contracts/http-api.md "Admin"). Each test gets an empty database.
/// </summary>
public abstract class ApiContract
{
    private const string NewPassword = "a-new-admin-password-1";

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Environment for an app on a fresh, empty database.</summary>
    protected abstract Task<Dictionary<string, string?>> FreshEnvAsync();

    /// <summary>The database password, which must never appear in logs.</summary>
    protected abstract string DatabasePassword { get; }

    /// <summary>Environment whose database password is wrong, for the failed-start check.</summary>
    protected abstract Task<Dictionary<string, string?>> WrongPasswordEnvAsync(string wrongPassword);

    private static AdminClient Client(ShopApp app) => new(app.CreateClient());

    private static async Task<AdminClient> SignedInAsync(ShopApp app, string password = ShopApp.AdminPassword)
    {
        var client = Client(app);
        using var login = await client.LoginAsync(password);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.NotNull(client.Xsrf);
        return client;
    }

    private static async Task<ItemJson> CreateAsync(AdminClient client, string name, int stock, string price)
    {
        using var response = await client.SendAsync(HttpMethod.Post, "/api/admin/items", new { name, stock, price });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ItemJson>(Ct))!;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>US1 scenario 1: the generated password reaches the admin area, which asks for a change.</summary>
    [Fact]
    public async Task GeneratedPasswordSignsIn()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var client = Client(app);

        using var wrong = await client.LoginAsync("not-the-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var anonymousMe = await client.SendAsync(HttpMethod.Get, "/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousMe.StatusCode);

        using var login = await client.LoginAsync(ShopApp.AdminPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True((await client.GetJsonAsync<MeJson>("/api/admin/me")).InitialPassword);
    }

    /// <summary>US1 scenarios 2–4: create, change (stock 0 → unavailable) and delete; orders stay unchanged.</summary>
    [Fact]
    public async Task AdminManagesItems()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var client = await SignedInAsync(app);

        var shirt = await CreateAsync(client, "Blue T-shirt", 12, "15.00");
        Assert.Equal(new ItemJson(shirt.Id, "Blue T-shirt", 1500, 12, true), shirt);
        await CreateAsync(client, "Red Cap", 3, "7.5");

        var list = await client.GetJsonAsync<ItemPageJson>("/api/admin/items");
        Assert.Equal(["Blue T-shirt", "Red Cap"], list.Items.Select(i => i.Name));
        Assert.Equal(2, list.Total);
        Assert.Equal(50, list.PageSize);
        Assert.Equal(["Red Cap"], (await client.GetJsonAsync<ItemPageJson>("/api/admin/items?q=CAP")).Items.Select(i => i.Name));

        using var update = await client.SendAsync(HttpMethod.Put, $"/api/admin/items/{shirt.Id}",
            new { name = "Blue T-shirt", stock = 0, price = "16" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(new ItemJson(shirt.Id, "Blue T-shirt", 1600, 0, false), await update.Content.ReadFromJsonAsync<ItemJson>(Ct));

        // An existing order with the item keeps its snapshot after the delete (FR-003).
        var store = app.Services.GetRequiredService<IShopStore>();
        var cap = list.Items.Single(i => i.Name == "Red Cap");
        var order = (await store.PlaceOrderAsync(new NewOrder(OrderReference.New(), DateTimeOffset.UtcNow, "0x1",
            [new OrderRequestLine(cap.Id, 1, 750)]), Ct)).Order!;

        using var delete = await client.SendAsync(HttpMethod.Delete, $"/api/admin/items/{cap.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var deleteAgain = await client.SendAsync(HttpMethod.Delete, $"/api/admin/items/{cap.Id}");
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
        using var updateMissing = await client.SendAsync(HttpMethod.Put, $"/api/admin/items/{cap.Id}",
            new { name = "Red Cap", stock = 1, price = "1" });
        Assert.Equal(HttpStatusCode.NotFound, updateMissing.StatusCode);

        Assert.Equal(["Blue T-shirt"], (await client.GetJsonAsync<ItemPageJson>("/api/admin/items")).Items.Select(i => i.Name));
        var stored = (await store.ListOrdersAsync(1, 20, Ct)).Items.Single();
        Assert.Equal(order.Lines, stored.Lines);
        Assert.Equal("Red Cap", stored.Lines.Single().Name);
    }

    /// <summary>US1 scenario 5: without a session every admin action is refused and nothing changes.</summary>
    [Fact]
    public async Task AnonymousAdminActionsAreRefused()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        var item = await CreateAsync(admin, "Shirt", 1, "1");
        using var anonymous = Client(app);
        anonymous.Xsrf = admin.Xsrf;

        var attempts = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/admin/me", null),
            (HttpMethod.Get, "/api/admin/items", null),
            (HttpMethod.Post, "/api/admin/items", new { name = "Hack", stock = 1, price = "1" }),
            (HttpMethod.Put, $"/api/admin/items/{item.Id}", new { name = "Hack", stock = 1, price = "1" }),
            (HttpMethod.Delete, $"/api/admin/items/{item.Id}", null),
            (HttpMethod.Post, "/api/admin/password", new { currentPassword = ShopApp.AdminPassword, newPassword = NewPassword }),
            (HttpMethod.Post, "/api/admin/logout", null),
        };
        foreach (var (method, path, body) in attempts)
        {
            using var response = await anonymous.SendAsync(method, path, body);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal([new ItemJson(item.Id, "Shirt", 100, 1, true)], (await admin.GetJsonAsync<ItemPageJson>("/api/admin/items")).Items);
        Assert.True((await admin.GetJsonAsync<MeJson>("/api/admin/me")).InitialPassword);
    }

    /// <summary>State-changing admin calls need the X-XSRF-TOKEN header (400 otherwise, nothing changes).</summary>
    [Fact]
    public async Task ChangesWithoutXsrfHeaderAreRefused()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var client = await SignedInAsync(app);
        var item = await CreateAsync(client, "Shirt", 1, "1");

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/api/admin/items", new { name = "Other", stock = 1, price = "1" }),
            (HttpMethod.Put, $"/api/admin/items/{item.Id}", new { name = "Changed", stock = 1, price = "1" }),
            (HttpMethod.Delete, $"/api/admin/items/{item.Id}", null),
            (HttpMethod.Post, "/api/admin/password", new { currentPassword = ShopApp.AdminPassword, newPassword = NewPassword }),
        })
        {
            using var missing = await client.SendAsync(method, path, body, xsrf: false);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }

        var saved = client.Xsrf;
        client.Xsrf = "forged";
        using var forged = await client.SendAsync(HttpMethod.Delete, $"/api/admin/items/{item.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        client.Xsrf = saved;

        Assert.Equal([new ItemJson(item.Id, "Shirt", 100, 1, true)], (await client.GetJsonAsync<ItemPageJson>("/api/admin/items")).Items);
        Assert.True((await client.GetJsonAsync<MeJson>("/api/admin/me")).InitialPassword);
    }

    /// <summary>US1 scenario 6 and FR-002: invalid input is refused with errors naming the field; 409 on duplicate names.</summary>
    [Fact]
    public async Task InvalidItemInputNamesTheField()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var client = await SignedInAsync(app);
        await CreateAsync(client, "Shirt", 1, "1");

        foreach (var (body, field) in new (object, string)[]
        {
            (new { name = "", stock = 1, price = "1" }, "name"),
            (new { name = new string('x', 101), stock = 1, price = "1" }, "name"),
            (new { name = "Ok", stock = -1, price = "1" }, "stock"),
            (new { name = "Ok", price = "1" }, "stock"),
            (new { name = "Ok", stock = 1, price = "0" }, "price"),
            (new { name = "Ok", stock = 1, price = "-5" }, "price"),
            (new { name = "Ok", stock = 1, price = "1.234" }, "price"),
        })
        {
            using var response = await client.SendAsync(HttpMethod.Post, "/api/admin/items", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await ProblemAsync(response);
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"expected an error for {field}");
        }

        using var duplicate = await client.SendAsync(HttpMethod.Post, "/api/admin/items", new { name = "SHIRT", stock = 1, price = "1" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.True((await ProblemAsync(duplicate)).GetProperty("errors").TryGetProperty("name", out _));
        Assert.Single((await client.GetJsonAsync<ItemPageJson>("/api/admin/items")).Items);
    }

    /// <summary>SC-006 / FR-006: after 5 failures the sixth sign-in is refused even with the right password.</summary>
    [Fact]
    public async Task SixthSignInIsRefusedEvenWhenCorrect()
    {
        var env = await FreshEnvAsync();
        await using var app = new ShopApp(env);
        using var client = Client(app);

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await client.LoginAsync("wrong-" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await client.LoginAsync(ShopApp.AdminPassword);

        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        var retryAfter = int.Parse(locked.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 890, 900);
        Assert.Null(client.SessionCookie);

        // The lock is in the store, so another replica refuses too.
        await using var other = new ShopApp(env);
        using var otherClient = Client(other);
        using var lockedThere = await otherClient.LoginAsync(ShopApp.AdminPassword);
        Assert.Equal((HttpStatusCode)429, lockedThere.StatusCode);
    }

    /// <summary>FR-007: a session from one instance works on another instance on the same store; sign-out ends it.</summary>
    [Fact]
    public async Task SessionWorksAcrossInstances()
    {
        var env = await FreshEnvAsync();
        await using var first = new ShopApp(env);
        using var client = await SignedInAsync(first);
        await using var second = new ShopApp(env);
        using var raw = second.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/admin/me", UriKind.Relative));
        request.Headers.Add("Cookie", client.SessionCookie);
        using var me = await raw.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var logout = await client.SendAsync(HttpMethod.Post, "/api/admin/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var afterLogout = await client.SendAsync(HttpMethod.Get, "/api/admin/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    /// <summary>Sends a copied session cookie (no cookie jar), as an attacker holding it would.</summary>
    private static async Task<HttpStatusCode> MeWithCookieAsync(ShopApp app, string? cookie)
    {
        using var raw = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/admin/me", UriKind.Relative));
        request.Headers.Add("Cookie", cookie);
        using var response = await raw.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>FR-007: sign-out raises the session version, so a stolen copy of the cookie stops working.</summary>
    [Fact]
    public async Task StolenCookieIsRefusedAfterSignOut()
    {
        var env = await FreshEnvAsync();
        await using var app = new ShopApp(env);
        await using var otherReplica = new ShopApp(env);
        using var client = await SignedInAsync(app);
        var stolen = client.SessionCookie;
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(app, stolen));

        using var logout = await client.SendAsync(HttpMethod.Post, "/api/admin/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(app, stolen));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(otherReplica, stolen));

        // Signing in again gives a working session with the new version.
        using var again = await SignedInAsync(app);
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(otherReplica, again.SessionCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(app, stolen));
    }

    /// <summary>
    /// A password change raises the session version: a stolen cookie stops working, while the
    /// browser that changed the password gets a fresh cookie and stays signed in.
    /// </summary>
    [Fact]
    public async Task StolenCookieIsRefusedAfterPasswordChange()
    {
        var env = await FreshEnvAsync();
        await using var app = new ShopApp(env);
        await using var otherReplica = new ShopApp(env);
        using var client = await SignedInAsync(app);
        var stolen = client.SessionCookie;

        using var changed = await client.SendAsync(HttpMethod.Post, "/api/admin/password",
            new { currentPassword = ShopApp.AdminPassword, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.NotEqual(stolen, client.SessionCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(app, stolen));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(otherReplica, stolen));

        // The changing browser keeps working, including XSRF-protected changes.
        Assert.False((await client.GetJsonAsync<MeJson>("/api/admin/me")).InitialPassword);
        await CreateAsync(client, "Shirt", 1, "1");
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(otherReplica, client.SessionCookie));
    }

    // ---- Public storefront API (US2) ----

    private static async Task<HttpResponseMessage> OrderAsync(HttpClient anonymous, params object[] lines) =>
        await anonymous.PostAsJsonAsync(new Uri("/api/orders", UriKind.Relative), new { lines }, Ct);

    /// <summary>FR-009, FR-010, US2 scenario 1: shop name and case-insensitive search, no sign-in.</summary>
    [Fact]
    public async Task StorefrontShowsShopNameAndSearches()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        await CreateAsync(admin, "Blue T-shirt", 12, "15.00");
        await CreateAsync(admin, "Red cap", 0, "5");
        using var anonymous = app.CreateClient();

        Assert.Equal("Acme", (await anonymous.GetFromJsonAsync<JsonElement>("/api/shop", Ct)).GetProperty("name").GetString());
        var shirts = await anonymous.GetFromJsonAsync<ItemPageJson>("/api/items?q=SHIRT", Ct);
        Assert.Equal(["Blue T-shirt"], shirts!.Items.Select(i => i.Name));
        var all = await anonymous.GetFromJsonAsync<ItemPageJson>("/api/items", Ct);
        Assert.Equal([true, false], all!.Items.Select(i => i.Available));
        using var badPage = await anonymous.GetAsync(new Uri("/api/items?page=0", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    /// <summary>
    /// US2 scenario 4: the order is stored as PendingPayment with lines, total, time and the shop's
    /// wallet; stock goes down by exactly the ordered quantities; duplicates are merged.
    /// </summary>
    [Fact]
    public async Task CustomerPlacesOrder()
    {
        var env = await FreshEnvAsync();
        await using var app = new ShopApp(env);
        using var admin = await SignedInAsync(app);
        var shirt = await CreateAsync(admin, "Blue T-shirt", 12, "15.00");
        var cap = await CreateAsync(admin, "Red cap", 3, "5.50");
        using var anonymous = app.CreateClient();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        using var response = await OrderAsync(anonymous,
            new { itemId = shirt.Id, quantity = 1, unitPriceCents = 1500 },
            new { itemId = cap.Id, quantity = 3, unitPriceCents = 550 },
            new { itemId = shirt.Id, quantity = 1, unitPriceCents = 1500 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var placed = (await response.Content.ReadFromJsonAsync<PlacedJson>(Ct))!;
        Assert.Equal("PendingPayment", placed.Status);
        Assert.Equal(4650, placed.TotalCents);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{10}$", placed.Reference);
        Assert.InRange(placed.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));

        var items = (await anonymous.GetFromJsonAsync<ItemPageJson>("/api/items", Ct))!.Items;
        Assert.Equal(10, items.Single(i => i.Id == shirt.Id).Stock);
        Assert.Equal(0, items.Single(i => i.Id == cap.Id).Stock);
        Assert.False(items.Single(i => i.Id == cap.Id).Available);

        var order = (await app.Services.GetRequiredService<IShopStore>().ListOrdersAsync(1, 20, Ct)).Items.Single();
        Assert.Equal(placed.Reference, order.Reference);
        Assert.Equal(env["WALLET_ADDRESS"], order.WalletAddress);
        Assert.Equal(
            [new OrderLine(shirt.Id, "Blue T-shirt", 1500, 2), new OrderLine(cap.Id, "Red cap", 550, 3)],
            order.Lines);
    }

    /// <summary>US2 scenario 5: changed price, too little stock or a deleted item → 409 with the differences, nothing stored.</summary>
    [Fact]
    public async Task ChangedCartIsRefusedWithDifferences()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        var shirt = await CreateAsync(admin, "Blue T-shirt", 12, "15.00");
        var cap = await CreateAsync(admin, "Red cap", 2, "5");
        var gone = await CreateAsync(admin, "Old hat", 5, "9");
        (await admin.SendAsync(HttpMethod.Put, $"/api/admin/items/{shirt.Id}", new { name = "Blue T-shirt", stock = 12, price = "16" })).Dispose();
        (await admin.SendAsync(HttpMethod.Delete, $"/api/admin/items/{gone.Id}")).Dispose();
        using var anonymous = app.CreateClient();

        using var response = await OrderAsync(anonymous,
            new { itemId = shirt.Id, quantity = 1, unitPriceCents = 1500 },
            new { itemId = cap.Id, quantity = 3, unitPriceCents = 500 },
            new { itemId = gone.Id, quantity = 1, unitPriceCents = 900 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problems = (await ProblemAsync(response)).GetProperty("problems").EnumerateArray()
            .ToDictionary(p => p.GetProperty("itemId").GetGuid());
        Assert.Equal("price", problems[shirt.Id].GetProperty("kind").GetString());
        Assert.Equal(1600, problems[shirt.Id].GetProperty("currentPriceCents").GetInt64());
        Assert.Equal("stock", problems[cap.Id].GetProperty("kind").GetString());
        Assert.Equal(2, problems[cap.Id].GetProperty("currentStock").GetInt32());
        Assert.False(problems[cap.Id].TryGetProperty("currentPriceCents", out _));
        Assert.Equal("removed", problems[gone.Id].GetProperty("kind").GetString());

        var store = app.Services.GetRequiredService<IShopStore>();
        Assert.Equal(0, (await store.ListOrdersAsync(1, 20, Ct)).Total);
        Assert.Equal(2, (await store.GetItemAsync(cap.Id, Ct))!.Stock);

        // The corrected cart goes through.
        using var retry = await OrderAsync(anonymous,
            new { itemId = shirt.Id, quantity = 1, unitPriceCents = 1600 },
            new { itemId = cap.Id, quantity = 2, unitPriceCents = 500 });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(2600, (await retry.Content.ReadFromJsonAsync<PlacedJson>(Ct))!.TotalCents);
    }

    /// <summary>US2 scenario 6: two customers order the last piece at once → exactly one succeeds.</summary>
    [Fact]
    public async Task LastPieceGoesToExactlyOneCustomer()
    {
        var env = await FreshEnvAsync();
        await using var app = new ShopApp(env);
        await using var otherReplica = new ShopApp(env);
        using var admin = await SignedInAsync(app);
        var item = await CreateAsync(admin, "Last one", 1, "10");
        using var a = app.CreateClient();
        using var b = otherReplica.CreateClient();

        var responses = await Task.WhenAll(
            OrderAsync(a, new { itemId = item.Id, quantity = 1, unitPriceCents = 1000 }),
            OrderAsync(b, new { itemId = item.Id, quantity = 1, unitPriceCents = 1000 }));

        try
        {
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
            var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
            var problem = (await ProblemAsync(loser)).GetProperty("problems").EnumerateArray().Single();
            Assert.Equal("stock", problem.GetProperty("kind").GetString());
            Assert.Equal(0, problem.GetProperty("currentStock").GetInt32());
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    /// <summary>The client can't choose the price; invalid requests are 400s; no sign-in or XSRF needed.</summary>
    [Fact]
    public async Task OrderRequestsAreValidated()
    {
        await using var app = new ShopApp(await FreshEnvAsync());
        using var admin = await SignedInAsync(app);
        var item = await CreateAsync(admin, "Shirt", 5, "15");
        using var anonymous = app.CreateClient();

        using var cheaper = await OrderAsync(anonymous, new { itemId = item.Id, quantity = 1, unitPriceCents = 1 });
        Assert.Equal(HttpStatusCode.Conflict, cheaper.StatusCode);

        foreach (var lines in new object[][]
                 {
                     [],
                     [new { itemId = item.Id, quantity = 0, unitPriceCents = 1500 }],
                     [new { itemId = item.Id, quantity = 1001, unitPriceCents = 1500 }],
                     [new { itemId = item.Id, quantity = 600, unitPriceCents = 1500 }, new { itemId = item.Id, quantity = 401, unitPriceCents = 1500 }],
                     [new { itemId = item.Id, quantity = 1, unitPriceCents = 0 }],
                     Enumerable.Range(0, 51).Select(_ => (object)new { itemId = Guid.NewGuid(), quantity = 1, unitPriceCents = 1 }).ToArray(),
                 })
        {
            using var response = await OrderAsync(anonymous, lines);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await ProblemAsync(response)).TryGetProperty("errors", out _));
        }

        using var noBody = await anonymous.PostAsync(new Uri("/api/orders", UriKind.Relative),
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);

        Assert.Equal(0, (await app.Services.GetRequiredService<IShopStore>().ListOrdersAsync(1, 20, Ct)).Total);
        Assert.Equal(5, (await app.Services.GetRequiredService<IShopStore>().GetItemAsync(item.Id, Ct))!.Stock);
    }

    /// <summary>
    /// POST /api/orders is rate-limited per client IP: over the limit → 429 with Retry-After and
    /// nothing stored; other clients and other endpoints are unaffected.
    /// </summary>
    [Fact]
    public async Task OrdersAreRateLimitedPerClientIp()
    {
        var env = await FreshEnvAsync();
        env["ORDER_RATE_LIMIT_PER_MINUTE"] = "3";
        await using var app = new ShopApp(env);
        using var admin = await SignedInAsync(app);
        var item = await CreateAsync(admin, "Shirt", 100, "1");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ShopApp.ClientIpHeader, "203.0.113.7");
        using var other = app.CreateClient();
        other.DefaultRequestHeaders.Add(ShopApp.ClientIpHeader, "198.51.100.9");
        var line = new { itemId = item.Id, quantity = 1, unitPriceCents = 100 };

        for (var i = 0; i < 3; i++)
        {
            using var ok = await OrderAsync(client, line);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var limited = await OrderAsync(client, line);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        var retryAfter = int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
        Assert.Equal(429, (await ProblemAsync(limited)).GetProperty("status").GetInt32());

        using var otherClient = await OrderAsync(other, line);
        Assert.Equal(HttpStatusCode.Created, otherClient.StatusCode);
        using var browsing = await client.GetAsync(new Uri("/api/items", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, browsing.StatusCode);

        Assert.Equal(4, (await app.Services.GetRequiredService<IShopStore>().ListOrdersAsync(1, 20, Ct)).Total);
        Assert.Equal(96, (await app.Services.GetRequiredService<IShopStore>().GetItemAsync(item.Id, Ct))!.Stock);
    }

    /// <summary>FR-005: the changed password replaces the generated one and survives a restart.</summary>
    [Fact]
    public async Task ChangedPasswordSurvivesRestart()
    {
        var env = await FreshEnvAsync();
        await using (var app = new ShopApp(env))
        {
            using var client = await SignedInAsync(app);

            using var tooShort = await client.SendAsync(HttpMethod.Post, "/api/admin/password",
                new { currentPassword = ShopApp.AdminPassword, newPassword = "short" });
            Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
            Assert.True((await ProblemAsync(tooShort)).GetProperty("errors").TryGetProperty("newPassword", out _));

            using var wrongCurrent = await client.SendAsync(HttpMethod.Post, "/api/admin/password",
                new { currentPassword = "wrong", newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.Unauthorized, wrongCurrent.StatusCode);

            using var changed = await client.SendAsync(HttpMethod.Post, "/api/admin/password",
                new { currentPassword = ShopApp.AdminPassword, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
            Assert.False((await client.GetJsonAsync<MeJson>("/api/admin/me")).InitialPassword);
        }

        await using var restarted = new ShopApp(env);
        using var fresh = Client(restarted);
        using var oldPassword = await fresh.LoginAsync(ShopApp.AdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        using var newPassword = await fresh.LoginAsync(NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, newPassword.StatusCode);
        Assert.False((await fresh.GetJsonAsync<MeJson>("/api/admin/me")).InitialPassword);
    }

    /// <summary>
    /// FR-024 / SC-007: no password, password hash or database credential in any log line, during
    /// normal use or a failed start with a wrong database password.
    /// </summary>
    [Fact]
    public async Task LogsContainNoSecrets()
    {
        var logs = new LogSink();
        string hash;
        await using (var app = new ShopApp(await FreshEnvAsync(), logs))
        {
            using var client = Client(app);
            (await client.LoginAsync("sent-wrong-password-1")).Dispose();
            (await client.LoginAsync(ShopApp.AdminPassword)).Dispose();
            hash = (await app.Services.GetRequiredService<IShopStore>().GetAdminAsync(Ct))!.PasswordHash;
            (await client.SendAsync(HttpMethod.Post, "/api/admin/password",
                new { currentPassword = ShopApp.AdminPassword, newPassword = NewPassword })).Dispose();
            await CreateAsync(client, "Shirt", 1, "1");
            (await client.SendAsync(HttpMethod.Post, "/api/admin/logout")).Dispose();
        }

        const string WrongDbPassword = "wrong-db-password-xyz";
        await using (var broken = new ShopApp(await WrongPasswordEnvAsync(WrongDbPassword), logs, TimeSpan.FromSeconds(8)))
        {
            Assert.ThrowsAny<Exception>(() => broken.CreateClient());
        }

        var all = string.Join('\n', logs.Lines);
        Assert.Contains("Waiting for the database", all, StringComparison.Ordinal);
        foreach (var secret in new[]
                 {
                     ShopApp.AdminPassword, NewPassword, "sent-wrong-password-1", hash, DatabasePassword, WrongDbPassword,
                 })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }
}

public sealed class PostgresApiTests(PostgresContainer db) : ApiContract, IClassFixture<PostgresContainer>
{
    protected override string DatabasePassword => db.Password;

    protected override async Task<Dictionary<string, string?>> FreshEnvAsync()
    {
        var env = ShopApp.BaseEnv();
        env["DATABASE_URL"] = await db.CreateDatabaseAsync("api_" + Guid.NewGuid().ToString("N"));
        return env;
    }

    protected override async Task<Dictionary<string, string?>> WrongPasswordEnvAsync(string wrongPassword)
    {
        var env = await FreshEnvAsync();
        env["DATABASE_URL"] = env["DATABASE_URL"]!.Replace(db.Password, wrongPassword, StringComparison.Ordinal);
        return env;
    }
}

public sealed class RedisApiTests(RedisContainer redis) : ApiContract, IClassFixture<RedisContainer>
{
    protected override string DatabasePassword => redis.Password;

    protected override async Task<Dictionary<string, string?>> FreshEnvAsync()
    {
        await using (var admin = await ConnectionMultiplexer.ConnectAsync(redis.Options(allowAdmin: true)))
        {
            await admin.GetServers().Single().FlushAllDatabasesAsync();
        }

        var env = ShopApp.BaseEnv();
        env["REDIS_URL"] = redis.Url;
        env["REDIS_PASSWORD"] = redis.Password;
        return env;
    }

    protected override async Task<Dictionary<string, string?>> WrongPasswordEnvAsync(string wrongPassword)
    {
        var env = await FreshEnvAsync();
        env["REDIS_PASSWORD"] = wrongPassword;
        return env;
    }
}
