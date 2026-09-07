using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HackerNews.BestStories.Api.Tests;

public sealed class ApiSmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task WeatherForecast_Endpoint_IsNotExposed()
    {
        var response = await _client.GetAsync("/weatherforecast", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BestStories_EndpointRouteSpace_IsReserved()
    {
        var response = await _client.GetAsync("/api/best-stories", TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
