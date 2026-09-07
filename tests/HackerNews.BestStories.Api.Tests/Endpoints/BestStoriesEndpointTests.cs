using System.Net;
using System.Net.Http.Json;
using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace HackerNews.BestStories.Api.Tests.Endpoints;

public sealed class BestStoriesEndpointTests
{
    [Fact]
    public async Task GetBestStories_EmitsExactPdfContract()
    {
        var story = HackerNews.BestStories.Api.Mapping.StoryMapper.Map(new HackerNewsItem
        {
            Id = 42, Type = "story", Title = "Sample", By = "author", Score = 1716,
            Descendants = 572, Time = new DateTimeOffset(2019, 10, 12, 13, 43, 1, TimeSpan.Zero).ToUnixTimeSeconds()
        });
        await using var factory = new ApiFactory(new StubBestStoriesService([story!]));
        using var client = factory.CreateClient();
        using var json = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/api/best-stories?n=1", TestContext.Current.CancellationToken));
        var item = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(["commentCount", "postedBy", "score", "time", "title", "uri"],
            item.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Sample", item.GetProperty("title").GetString());
        Assert.Equal("https://news.ycombinator.com/item?id=42", item.GetProperty("uri").GetString());
        Assert.Equal("author", item.GetProperty("postedBy").GetString());
        Assert.Equal("2019-10-12T13:43:01+00:00", item.GetProperty("time").GetString());
        Assert.Equal(1716, item.GetProperty("score").GetInt32());
        Assert.Equal(572, item.GetProperty("commentCount").GetInt32());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(3)]
    public async Task GetBestStories_ReturnsBadRequest_WhenNIsNotProvided(int maxStories)
    {
        var service = new StubBestStoriesService([]);
        await using var baseFactory = new ApiFactory(service);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HackerNews:MaxStories"] = maxStories.ToString()
                })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/best-stories", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("Invalid n", problem.Title);
        Assert.Equal(
            $"Query parameter 'n' is required. Supply an integer between 1 and {maxStories}, for example /api/best-stories?n=1.",
            problem.Detail);
        Assert.Equal(400, problem.Status);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData("/api/best-stories?n=1", 1)]
    [InlineData("/api/best-stories?n=10", 10)]
    [InlineData("/api/best-stories?n=200", 200)]
    [InlineData("/api/best-stories?n=500", 500)]
    public async Task GetBestStories_ReturnsOk_ForValidN(string url, int expectedCount)
    {
        var stories = Enumerable.Range(1, 500)
            .Select(id => new StoryDto { Id = id, Title = $"Story {id}", Score = 501 - id })
            .ToArray();
        var service = new StubBestStoriesService(stories);
        await using var factory = new ApiFactory(service);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<StoryDto[]>(TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        Assert.Equal(expectedCount, payload.Length);
        Assert.Equal(stories.Take(expectedCount).Select(story => story.Title), payload.Select(story => story.Title));
        Assert.Equal(1, service.Calls);
    }

    [Theory]
    [InlineData("/api/best-stories?n=-1")]
    [InlineData("/api/best-stories?n=0")]
    [InlineData("/api/best-stories?n=501")]
    public async Task GetBestStories_ReturnsBadRequest_ForOutOfRangeN(string url)
    {
        var service = new StubBestStoriesService([]);
        await using var factory = new ApiFactory(service);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(problem);
        Assert.Equal("Invalid n", problem.Title);
        Assert.Equal("n must be between 1 and 500.", problem.Detail);
        Assert.Equal(400, problem.Status);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task GetBestStories_ReturnsBadRequest_WithBoundMessage_ForOutOfRangeN()
    {
        var service = new StubBestStoriesService([]);
        await using var factory = new ApiFactory(service);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/best-stories?n=501", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"title\":\"Invalid n\"", payload);
        Assert.Contains("\"detail\":\"n must be between 1 and 500.\"", payload);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public async Task GetBestStories_ReturnsBadRequest_ForMalformedN(string n)
    {
        var service = new StubBestStoriesService([]);
        await using var baseFactory = new ApiFactory(service);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/best-stories?n={n}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GetBestStories_UsesConfiguredMaximum(int n)
    {
        var service = new StubBestStoriesService([
            .. Enumerable.Range(1, 4)
                .Select(id => new StoryDto { Id = id, Title = $"Story {id}", Score = 5 - id })
        ]);
        await using var baseFactory = new ApiFactory(service);
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["HackerNews:MaxStories"] = "3"
                })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/best-stories?n={n}", TestContext.Current.CancellationToken);

        if (n == 3)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<StoryDto[]>(TestContext.Current.CancellationToken);
            Assert.NotNull(payload);
            Assert.Equal(["Story 1", "Story 2", "Story 3"], payload.Select(story => story.Title));
            Assert.Equal(1, service.Calls);
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
            Assert.NotNull(problem);
            Assert.Equal("Invalid n", problem.Title);
            Assert.Equal("n must be between 1 and 3.", problem.Detail);
            Assert.Equal(400, problem.Status);
            Assert.Equal(0, service.Calls);
        }
    }

    [Theory]
    [InlineData("/api/best-stories/10")]
    [InlineData("/api/v1/best-stories?n=1")]
    [InlineData("/api/v1/best-stories/1")]
    [InlineData("/api/v2/best-stories?n=1")]
    [InlineData("/api/v2/best-stories/1")]
    public async Task RemovedRouteShapes_ReturnNotFound(string url)
    {
        var service = new StubBestStoriesService([]);
        await using var factory = new ApiFactory(service);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task SwaggerEndpoint_Redirects_ToTrailingSlash()
    {
        await using var factory = new ApiFactory(new StubBestStoriesService([]));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/swagger", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("swagger/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task SwaggerEndpoint_ReturnsInteractiveHtml()
    {
        await using var factory = new ApiFactory(new StubBestStoriesService([]));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("scalar", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiAndSwagger_AreUnavailable_WhenOpenApiIsDisabled()
    {
        await using var baseFactory = new ApiFactory(new StubBestStoriesService([]));
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenApi:Enabled"] = "false"
                })));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var swaggerResponse = await client.GetAsync("/swagger", TestContext.Current.CancellationToken);
        var openApiResponse = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, swaggerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, openApiResponse.StatusCode);
    }
}
