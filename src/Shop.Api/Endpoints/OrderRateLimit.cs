using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Shop.Api.Endpoints;

/// <summary>
/// Limits <c>POST /api/orders</c> per client IP (built-in ASP.NET Core rate limiting), so one
/// client can't tie up stock with a flood of unpaid orders. Over the limit: 429 problem details
/// with <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// The window is per replica (in memory), so with N replicas a client gets up to N × the limit.
/// The key is the TCP peer address: forwarded headers are not trusted, because nothing in front
/// of the shop sets them reliably.
/// </remarks>
public static class OrderRateLimit
{
    public const string Policy = "orders";

    /// <summary>Optional override, e.g. for tests; default <see cref="DefaultPermitsPerMinute"/>.</summary>
    public const string PermitsSetting = "ORDER_RATE_LIMIT_PER_MINUTE";

    public const int DefaultPermitsPerMinute = 10;

    public static IServiceCollection AddOrderRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IConfiguration>((o, configuration) =>
        {
            var permits = configuration.GetValue(PermitsSetting, DefaultPermitsPerMinute);
            if (permits < 1)
            {
                throw new SettingsException($"Invalid configuration: {PermitsSetting} must be 1 or more.");
            }

            o.AddPolicy(Policy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            o.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    http.Response.Headers.RetryAfter =
                        Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = { Status = 429, Title = "Too many orders from your address. Try again shortly." },
                });
            };
        });
        return services;
    }
}
