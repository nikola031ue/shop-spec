using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Shop.Api.Domain;
using Shop.Api.Storage;

namespace Shop.Api.Auth;

/// <summary>Marker type for <see cref="PasswordHasher{TUser}"/>; the shop has one admin and no user table.</summary>
public sealed class AdminUser
{
    internal static readonly AdminUser Instance = new();
}

public enum SignInStatus
{
    Success,
    WrongPassword,
    Locked,
}

/// <summary>On success, <see cref="SessionVersion"/> is the version the new session cookie must carry.</summary>
public sealed record SignInResult(SignInStatus Status, TimeSpan RetryAfter = default, long SessionVersion = 0);

public enum ChangePasswordStatus
{
    Success,
    TooShort,
    WrongCurrentPassword,
}

/// <summary>
/// The single admin account (research R11): bootstrap from <c>ADMIN_PASSWORD</c>, PBKDF2 hashes via
/// <see cref="PasswordHasher{TUser}"/>, lockout held in the store so it applies across replicas.
/// </summary>
public sealed class AdminAuth(IShopStore store, IPasswordHasher<AdminUser> hasher, TimeProvider time)
{
    public const int MinPasswordLength = 12;
    public const string Policy = "admin";

    public static LockoutPolicy Lockout => LockoutPolicy.Default;

    /// <summary>
    /// Admin bootstrap rules, run under the store's init lock:
    /// no record → create from <paramref name="adminPassword"/> with <c>initial=true</c>;
    /// <c>initial=true</c> but the hash doesn't match (Secret regenerated) → re-hash;
    /// <c>initial=false</c> → never touched. Returns the record to write, or null.
    /// </summary>
    public static AdminRecord? Bootstrap(AdminRecord? existing, string adminPassword, IPasswordHasher<AdminUser> hasher)
    {
        if (existing is null)
        {
            return new AdminRecord(hasher.HashPassword(AdminUser.Instance, adminPassword), true, null, 0, null, null);
        }

        if (!existing.Initial || Verify(hasher, existing.PasswordHash, adminPassword))
        {
            return null;
        }

        // The password changed, so sessions issued with the old one end too.
        return existing with
        {
            PasswordHash = hasher.HashPassword(AdminUser.Instance, adminPassword),
            SessionVersion = existing.SessionVersion + 1,
        };
    }

    /// <summary>
    /// Checks the password unless sign-in is locked. A wrong password counts towards the lockout;
    /// the attempt that triggers the lock still reports <see cref="SignInStatus.WrongPassword"/>.
    /// </summary>
    public async Task<SignInResult> SignInAsync(string? password, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var admin = await store.GetAdminAsync(ct) ?? throw new InvalidOperationException("The admin record is missing.");
        if (Lockout.IsLocked(admin, now, out var retryAfter))
        {
            return new SignInResult(SignInStatus.Locked, retryAfter);
        }

        if (!string.IsNullOrEmpty(password) && Verify(hasher, admin.PasswordHash, password))
        {
            if (admin.FailedCount > 0 || admin.FailedWindowStart is not null || admin.LockedUntil is not null)
            {
                await store.ResetFailedSignInsAsync(ct);
            }

            return new SignInResult(SignInStatus.Success, SessionVersion: admin.SessionVersion);
        }

        await store.RecordFailedSignInAsync(Lockout, now, ct);
        return new SignInResult(SignInStatus.WrongPassword);
    }

    public async Task<bool> IsInitialPasswordAsync(CancellationToken ct) =>
        (await store.GetAdminAsync(ct))?.Initial ?? true;

    /// <summary>Ends every existing admin session (sign-out): their cookies carry an older version now.</summary>
    public Task<long> EndAllSessionsAsync(CancellationToken ct) => store.BumpSessionVersionAsync(ct);

    public async Task<long> CurrentSessionVersionAsync(CancellationToken ct) =>
        (await store.GetAdminAsync(ct))?.SessionVersion ?? throw new InvalidOperationException("The admin record is missing.");

    /// <summary>
    /// Changes the password (≥ 12 characters); sets <c>changedAt</c> and <c>initial=false</c> and
    /// increments the session version, so every existing session ends.
    /// </summary>
    public async Task<ChangePasswordStatus> ChangePasswordAsync(string? current, string? replacement, CancellationToken ct)
    {
        if (replacement is null || replacement.Length < MinPasswordLength)
        {
            return ChangePasswordStatus.TooShort;
        }

        var admin = await store.GetAdminAsync(ct) ?? throw new InvalidOperationException("The admin record is missing.");
        if (string.IsNullOrEmpty(current) || !Verify(hasher, admin.PasswordHash, current))
        {
            return ChangePasswordStatus.WrongCurrentPassword;
        }

        await store.SetAdminPasswordAsync(hasher.HashPassword(AdminUser.Instance, replacement), time.GetUtcNow(), ct);
        return ChangePasswordStatus.Success;
    }

    private static bool Verify(IPasswordHasher<AdminUser> hasher, string hash, string password) =>
        hasher.VerifyHashedPassword(AdminUser.Instance, hash, password) != PasswordVerificationResult.Failed;
}

/// <summary>Cookie session, antiforgery and store-backed Data-Protection keys for the admin API.</summary>
public static class AdminAuthSetup
{
    public const string SessionCookie = "shop.session";
    public const string XsrfCookie = "XSRF-TOKEN";
    public const string XsrfHeader = "X-XSRF-TOKEN";

    /// <summary>Claim holding the admin session version the cookie was issued with.</summary>
    public const string SessionVersionClaim = "shop:session-version";

    /// <summary>Signs the admin in with a cookie bound to <paramref name="sessionVersion"/>, then issues the XSRF cookie.</summary>
    public static async Task SignInAsync(HttpContext context, long sessionVersion)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(SessionVersionClaim, sessionVersion.ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        // The XSRF token is bound to the signed-in user, so issue it for the new principal.
        context.User = principal;
        IssueXsrfCookie(context, context.RequestServices.GetRequiredService<IAntiforgery>());
    }

    /// <summary>
    /// Refuses a session cookie whose version is older than the admin record's (after sign-out or a
    /// password change). Checked on admin API requests only, so storefront and static requests
    /// don't read the store.
    /// </summary>
    private static async Task ValidateSessionVersionAsync(CookieValidatePrincipalContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/admin"))
        {
            return;
        }

        var store = context.HttpContext.RequestServices.GetRequiredService<IShopStore>();
        var admin = await store.GetAdminAsync(context.HttpContext.RequestAborted);
        var claim = context.Principal?.FindFirst(SessionVersionClaim)?.Value;
        if (admin is null || claim != admin.SessionVersion.ToString(CultureInfo.InvariantCulture))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }

    public static IServiceCollection AddAdminAuth(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<AdminUser>, PasswordHasher<AdminUser>>();
        services.AddSingleton<AdminAuth>();

        // Keys live in the store so every replica can read every cookie (FR-007).
        services.AddDataProtection().SetApplicationName("shophub-shop");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IShopStore>((o, store) => o.XmlRepository = new StoreXmlRepository(store));

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = SessionCookie;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = false;
                o.Events.OnValidatePrincipal = ValidateSessionVersionAsync;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminAuth.Policy, p => p.RequireAuthenticatedUser());

        services.AddAntiforgery(o =>
        {
            o.HeaderName = XsrfHeader;
            o.Cookie.Name = "shop.af";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        return services;
    }

    /// <summary>
    /// Writes the Angular-readable <c>XSRF-TOKEN</c> cookie for the current user. Call it after
    /// sign-in (with <c>HttpContext.User</c> already set) and when the admin UI loads.
    /// </summary>
    public static void IssueXsrfCookie(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(XsrfCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
        });
    }

    /// <summary>Requires a valid <c>X-XSRF-TOKEN</c> header on state-changing admin endpoints (400 otherwise).</summary>
    public static TBuilder RequireXsrf<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var antiforgery = ctx.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(ctx.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing or invalid XSRF token.");
            }

            return await next(ctx);
        });
}
