using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Shop.Api.Auth;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Endpoints;

/// <summary>Admin API (contracts/http-api.md "Admin"): session, password and item management.</summary>
public static class AdminEndpoints
{
    public const int ItemPageSize = 50;
    public const int OrderPageSize = 20;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/admin");
        api.MapPost("/login", LoginAsync);

        // Authorization runs before the XSRF filter: no session → 401, session without token → 400.
        var admin = api.MapGroup("").RequireAuthorization(AdminAuth.Policy);
        admin.MapPost("/logout", LogoutAsync).RequireXsrf();
        admin.MapGet("/me", MeAsync);
        admin.MapPost("/password", ChangePasswordAsync).RequireXsrf();
        admin.MapGet("/items", ListItemsAsync);
        admin.MapPost("/items", CreateItemAsync).RequireXsrf();
        admin.MapPut("/items/{id:guid}", UpdateItemAsync).RequireXsrf();
        admin.MapDelete("/items/{id:guid}", DeleteItemAsync).RequireXsrf();
        admin.MapGet("/orders", ListOrdersAsync);
        admin.MapGet("/payments", ListPaymentsAsync);
        return app;
    }

    public sealed record LoginRequest(string? Password);

    public sealed record PasswordRequest(string? CurrentPassword, string? NewPassword);

    public sealed record ItemRequest(string? Name, int? Stock, string? Price);

    public sealed record ItemDto(Guid Id, string Name, long PriceCents, int Stock, bool Available)
    {
        public static ItemDto From(Item item) => new(item.Id, item.Name, item.PriceCents, item.Stock, item.Available);
    }

    public sealed record ItemPageDto(IReadOnlyList<ItemDto> Items, int Page, int PageSize, long Total);

    public sealed record MeDto(bool InitialPassword);

    public sealed record OrderLineDto(Guid ItemId, string Name, long UnitPriceCents, int Quantity);

    /// <summary>Spec 003's order plus the payment state (spec 005 contracts/shop-api.md "Admin").</summary>
    public sealed record OrderDto(
        string Reference,
        DateTimeOffset CreatedAt,
        string Status,
        long TotalCents,
        string WalletAddress,
        IReadOnlyList<OrderLineDto> Lines,
        string TotalBaseUnits,
        string? PayingAccount,
        DateTimeOffset Deadline,
        [property: System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PublicEndpoints.PaymentDto? Payment)
    {
        public static OrderDto From(Order o, Settings settings) => new(o.Reference, o.CreatedAt, o.Status, o.TotalCents,
            o.WalletAddress, o.Lines.Select(l => new OrderLineDto(l.ItemId, l.Name, l.UnitPriceCents, l.Quantity)).ToList(),
            BaseUnits.Format(o.TotalBaseUnits), o.PayingAccount, o.Deadline,
            o.Payment is { } p ? new PublicEndpoints.PaymentDto(p.Tx, p.Block, p.PaidAt, settings.ExplorerTxUrl + p.Tx) : null);
    }

    public sealed record OrderPageDto(IReadOnlyList<OrderDto> Orders, int Page, int PageSize, long Total);

    /// <summary>A recorded transfer (contracts/shop-api.md "Admin" <c>/api/admin/payments</c>).</summary>
    public sealed record PaymentDto(
        string Tx,
        int LogIndex,
        long Block,
        DateTimeOffset BlockTime,
        string From,
        string To,
        string ValueBaseUnits,
        string? OrderReference,
        string? Reason,
        string ExplorerUrl);

    public sealed record PaymentPageDto(IReadOnlyList<PaymentDto> Payments, int Page, int PageSize, long Total);

    private static async Task<IResult> LoginAsync(
        LoginRequest request, AdminAuth auth, HttpContext context, CancellationToken ct)
    {
        var result = await auth.SignInAsync(request.Password, ClientAddress.Of(context), ct);
        switch (result.Status)
        {
            case SignInStatus.Locked:
                context.Response.Headers.RetryAfter =
                    Math.Ceiling(result.RetryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many failed sign-ins. Try again later.");
            case SignInStatus.WrongPassword:
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Wrong password.");
        }

        await AdminAuthSetup.SignInAsync(context, result.SessionVersion);
        return Results.NoContent();
    }

    /// <summary>Ends this and every other admin session: copies of the cookie stop working too.</summary>
    private static async Task<IResult> LogoutAsync(AdminAuth auth, HttpContext context, CancellationToken ct)
    {
        await auth.EndAllSessionsAsync(ct);
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        context.Response.Cookies.Delete(AdminAuthSetup.XsrfCookie);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        AdminAuth auth, IAntiforgery antiforgery, HttpContext context, CancellationToken ct)
    {
        // Refreshes the XSRF cookie, so a reloaded admin UI can make changes.
        AdminAuthSetup.IssueXsrfCookie(context, antiforgery);
        return Results.Ok(new MeDto(await auth.IsInitialPasswordAsync(ct)));
    }

    /// <summary>
    /// A successful change ends every existing session (new session version) and signs this browser
    /// in again with the new version.
    /// </summary>
    private static async Task<IResult> ChangePasswordAsync(
        PasswordRequest request, AdminAuth auth, HttpContext context, CancellationToken ct)
    {
        switch (await auth.ChangePasswordAsync(request.CurrentPassword, request.NewPassword, ct))
        {
            case ChangePasswordStatus.TooShort:
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newPassword"] = [$"The new password must have at least {AdminAuth.MinPasswordLength} characters."],
                });
            case ChangePasswordStatus.WrongCurrentPassword:
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The current password is wrong.");
        }

        await AdminAuthSetup.SignInAsync(context, await auth.CurrentSessionVersionAsync(ct));
        return Results.NoContent();
    }

    /// <summary>Item list, shared by the public and admin APIs (same shape, contracts/http-api.md).</summary>
    internal static async Task<IResult> ListItemsAsync(IShopStore store, CancellationToken ct, string? q = null, int page = 1)
    {
        if (page < 1)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be 1 or more."] });
        }

        var result = await store.ListItemsAsync(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), page, ItemPageSize, ct);
        return Results.Ok(new ItemPageDto(result.Items.Select(ItemDto.From).ToList(), page, ItemPageSize, result.Total));
    }

    private static async Task<IResult> CreateItemAsync(
        ItemRequest request, IShopStore store, TimeProvider time, CancellationToken ct)
    {
        var (draft, errors) = ItemRules.Validate(request.Name, request.Stock, request.Price);
        if (draft is null)
        {
            return Results.ValidationProblem(errors);
        }

        var write = await store.CreateItemAsync(Guid.CreateVersion7(), draft, time.GetUtcNow(), ct);
        return write.Status == ItemWriteStatus.Ok
            ? Results.Created($"/api/admin/items/{write.Item!.Id}", ItemDto.From(write.Item))
            : NameExists();
    }

    private static async Task<IResult> UpdateItemAsync(
        Guid id, ItemRequest request, IShopStore store, TimeProvider time, CancellationToken ct)
    {
        var (draft, errors) = ItemRules.Validate(request.Name, request.Stock, request.Price);
        if (draft is null)
        {
            return Results.ValidationProblem(errors);
        }

        var write = await store.UpdateItemAsync(id, draft, time.GetUtcNow(), ct);
        return write.Status switch
        {
            ItemWriteStatus.Ok => Results.Ok(ItemDto.From(write.Item!)),
            ItemWriteStatus.NotFound => ItemNotFound(),
            _ => NameExists(),
        };
    }

    private static async Task<IResult> DeleteItemAsync(Guid id, IShopStore store, CancellationToken ct) =>
        await store.DeleteItemAsync(id, ct) ? Results.NoContent() : ItemNotFound();

    /// <summary>US3: all orders newest first, 20 per page, with the lines as stored at ordering time.</summary>
    private static async Task<IResult> ListOrdersAsync(IShopStore store, Settings settings, CancellationToken ct, int page = 1)
    {
        if (page < 1)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be 1 or more."] });
        }

        var result = await store.ListOrdersAsync(page, OrderPageSize, ct);
        return Results.Ok(new OrderPageDto(
            result.Items.Select(o => OrderDto.From(o, settings)).ToList(), page, OrderPageSize, result.Total));
    }

    /// <summary>Spec 005 FR-007, FR-016: recorded transfers, newest first; <c>matched=false</c> lists only unmatched ones.</summary>
    private static async Task<IResult> ListPaymentsAsync(
        IShopStore store, Settings settings, CancellationToken ct, int page = 1, bool? matched = null)
    {
        if (page < 1)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be 1 or more."] });
        }

        var result = await store.ListPaymentsAsync(matched == false, page, OrderPageSize, ct);
        return Results.Ok(new PaymentPageDto(
            result.Items.Select(p => new PaymentDto(p.Tx, p.LogIndex, p.Block, p.BlockTime, p.FromAccount, p.ToAddress,
                BaseUnits.Format(p.Value), p.OrderReference, p.UnmatchedReason, settings.ExplorerTxUrl + p.Tx)).ToList(),
            page, OrderPageSize, result.Total));
    }

    private static IResult ItemNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found.");

    private static IResult NameExists() => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "An item with this name already exists.",
        extensions: new Dictionary<string, object?>
        {
            ["errors"] = new Dictionary<string, string[]> { ["name"] = ["An item with this name already exists."] },
        });
}
