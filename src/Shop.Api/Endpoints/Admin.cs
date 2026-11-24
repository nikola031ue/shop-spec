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

    private static async Task<IResult> LoginAsync(
        LoginRequest request, AdminAuth auth, HttpContext context, CancellationToken ct)
    {
        var result = await auth.SignInAsync(request.Password, ct);
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

    private static async Task<IResult> ListItemsAsync(IShopStore store, CancellationToken ct, string? q = null, int page = 1)
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
