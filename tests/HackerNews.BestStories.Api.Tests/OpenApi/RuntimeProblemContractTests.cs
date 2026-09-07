using System.Text.Json;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Errors;
using HackerNews.BestStories.Api.Tests.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNews.BestStories.Api.Tests.OpenApi;

public sealed class RuntimeProblemContractTests
{
    [Theory]
    [InlineData(400, "")]
    [InlineData(400, "?n=abc")]
    [InlineData(400, "?n=2147483648")]
    [InlineData(400, "?n=0")]
    [InlineData(400, "?n=501")]
    [InlineData(429, "?n=1")]
    [InlineData(500, "?n=1")]
    [InlineData(502, "?n=1")]
    [InlineData(504, "?n=1")]
    public async Task ErrorResponses_MatchLiveAndPublishedProblemContract(int status, string query)
    {
        var upstream = new CompleteSnapshotTests.Upstream();
        if (status >= 500)
            upstream.Item = (_, _) => throw (status switch
            {
                502 => new UpstreamException("secret upstream details"),
                504 => new UpstreamTimeoutException("secret upstream details"),
                _ => new InvalidOperationException("secret application details")
            });
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IHackerNewsClient>(upstream)));
        using var client = factory.CreateClient();
        if (status == 429)
            for (var request = 0; request < 60; request++)
            {
                using var accepted = await client.GetAsync("/api/best-stories?n=1");
                Assert.Equal(200, (int)accepted.StatusCode);
                Assert.Equal("application/json", accepted.Content.Headers.ContentType!.MediaType);
            }

        using var response = await client.GetAsync("/api/best-stories" + query);
        Assert.Equal(status, (int)response.StatusCode);
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.Equal("application/problem+json", mediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(status, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        Assert.DoesNotContain("secret", body);
        Assert.Equal(status == 400 ? 0 : 1, upstream.IdCalls);

        using var live = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        using var published = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "OpenApi", "published-v1.json")));
        foreach (var document in new[] { live, published })
        {
            var content = document.RootElement.GetProperty("paths").GetProperty("/api/best-stories")
                .GetProperty("get").GetProperty("responses").GetProperty(status.ToString()).GetProperty("content");
            Assert.Equal(mediaType, Assert.Single(content.EnumerateObject()).Name);
            Assert.Equal("#/components/schemas/ProblemDetails",
                content.GetProperty(mediaType!).GetProperty("schema").GetProperty("$ref").GetString());
        }
    }
}