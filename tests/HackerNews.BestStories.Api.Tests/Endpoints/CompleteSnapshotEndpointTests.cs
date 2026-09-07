using System.Net;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Configuration;
using HackerNews.BestStories.Api.Errors;
using HackerNews.BestStories.Api.Services;
using HackerNews.BestStories.Api.Tests.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HackerNews.BestStories.Api.Tests.Endpoints;

[Collection(StartupValidationCollection.Name)]
public sealed class CompleteSnapshotEndpointTests
{
    [Fact]
    public async Task RealService_RefreshDeadline_Returns504()
    {
        var clock = new CompleteSnapshotTests.ManualTime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new CompleteSnapshotTests.Upstream
        {
            Item = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; }
        };
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHackerNewsClient>();
            services.AddSingleton<IHackerNewsClient>(upstream);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<HackerNewsOptions>(options => options.RefreshTimeoutSeconds = 2);
        }));
        using var client = factory.CreateClient();
        var response = client.GetAsync("/api/best-stories?n=1", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await response.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.BadGateway)]
    [InlineData(true, HttpStatusCode.GatewayTimeout)]
    public async Task RealService_PartialFailure_Returns502Or504_ThenStaleSuccess(bool timeout, HttpStatusCode expected)
    {
        var clock = new CompleteSnapshotTests.ManualTime();
        var upstream = new CompleteSnapshotTests.Upstream
        {
            Item = (id, _) => id == 2
                ? throw (timeout ? new UpstreamTimeoutException("timeout") : new HttpRequestException("failure"))
                : Task.FromResult<HackerNews.BestStories.Api.Models.HackerNewsItem?>(CompleteSnapshotTests.Story(id))
        };
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHackerNewsClient>();
            services.AddSingleton<IHackerNewsClient>(upstream);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var client = factory.CreateClient();
        Assert.Equal(expected, (await client.GetAsync("/api/best-stories?n=2", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(expected, (await client.GetAsync("/api/best-stories?n=2", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(1, upstream.IdCalls);
        clock.Advance(TimeSpan.FromSeconds(30));
        upstream.Item = (id, _) => Task.FromResult<HackerNews.BestStories.Api.Models.HackerNewsItem?>(CompleteSnapshotTests.Story(id));
        var success = await client.GetStringAsync("/api/best-stories?n=2", TestContext.Current.CancellationToken);
        upstream.Item = (_, _) => throw new HttpRequestException("partial failure");
        await factory.Services.GetRequiredService<IBestStoriesService>().RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(success, await client.GetStringAsync("/api/best-stories?n=2", TestContext.Current.CancellationToken));
        Assert.Equal(3, upstream.IdCalls);
    }

    [Theory]
    [InlineData("MaxStories")]
    [InlineData("CacheTtlSeconds")]
    [InlineData("FailureCacheTtlSeconds")]
    [InlineData("MaxConcurrency")]
    [InlineData("RequestTimeoutSeconds")]
    [InlineData("RefreshTimeoutSeconds")]
    [InlineData("BaseUrl")]
    public void Startup_RejectsInvalidOptions(string property)
    {
        StartupValidationAssert.ThrowsOptionsValidation(() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.PostConfigure<HackerNewsOptions>(options =>
                        typeof(HackerNewsOptions).GetProperty(property)!
                            .SetValue(options, property == "BaseUrl" ? "file:///invalid" : (object)0)))));
    }

    [Fact]
    public void HttpClient_UsesConfiguredRequestTimeout()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<HackerNewsOptions>(options => options.RequestTimeoutSeconds = 7)));
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IHackerNewsClient));
        Assert.Equal(TimeSpan.FromSeconds(7), client.Timeout);
    }
}
