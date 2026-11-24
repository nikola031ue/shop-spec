using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Shop.Api.Tests.Unit;

/// <summary>Scaffold check: the host starts and serves the health endpoint.</summary>
public sealed class SmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task HealthzReturnsOk()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
