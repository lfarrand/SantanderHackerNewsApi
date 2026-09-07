using HackerNews.BestStories.Blazor.Tests.Infrastructure;
using HackerNews.BestStories.Blazor.Services;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HackerNews.BestStories.Blazor.Tests.Startup;

public sealed class ProgramStartupTests
{
    [Fact]
    public async Task Startup_UsesDefaultApiBaseUrlFallback_AndHttpClientTimeout()
    {
        await using var factory = new BlazorAppFactory();

        using var scope = factory.Services.CreateScope();
        var httpClient = CreateTypedHttpClient(scope.ServiceProvider);

        Assert.Equal(new Uri("http://localhost:5182/"), httpClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(130), httpClient.Timeout);
    }

    [Fact]
    public async Task Startup_NormalizesConfiguredApiBaseUrlWithoutTrailingSlash_AndHttpClientTimeout()
    {
        await using var factory = new BlazorAppFactory(apiBaseUrl: "http://example.local:5000");

        using var scope = factory.Services.CreateScope();
        var httpClient = CreateTypedHttpClient(scope.ServiceProvider);

        Assert.Equal(new Uri("http://example.local:5000/"), httpClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(130), httpClient.Timeout);
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(-1, 120)]
    [InlineData(60, 120)]
    [InlineData(120, 120)]
    [InlineData(130, 0)]
    [InlineData(int.MaxValue, 120)]
    public void Startup_RejectsTimeoutWithoutRefreshHeadroom(int requestSeconds, int refreshSeconds)
    {
        using var factory = new BlazorAppFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiClient:RequestTimeoutSeconds"] = requestSeconds.ToString(),
                ["ApiClient:RefreshTimeoutSeconds"] = refreshSeconds.ToString()
            })));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData(130, 120)]
    [InlineData(250, 240)]
    public async Task ConfiguredClient_AllowsDelayedCompletion_WithRefreshHeadroom(int requestSeconds, int refreshSeconds)
    {
        using var handler = new GatedHandler();
        await using var factory = new BlazorAppFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiClient:RequestTimeoutSeconds"] = requestSeconds.ToString(),
                ["ApiClient:RefreshTimeoutSeconds"] = refreshSeconds.ToString()
            }));
            builder.ConfigureServices(services => services.AddHttpClient<BestStoriesClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler));
        });
        using var httpClient = CreateTypedHttpClient(factory.Services);
        Assert.Equal(TimeSpan.FromSeconds(requestSeconds), httpClient.Timeout);
        Assert.True(httpClient.Timeout > TimeSpan.FromSeconds(refreshSeconds));
        var result = factory.Services.GetRequiredService<BestStoriesClient>().GetBestStoriesAsync(1);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.IsCompleted);
        handler.Release.SetResult();
        var story = Assert.Single(await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Delayed snapshot", story.Title);
        Assert.Equal("https://example.test/story", story.Uri);
        Assert.Equal(1, handler.Calls);
        Assert.False(handler.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task Startup_MapsRootEndpoint_AndReturnsSuccess()
    {
        await using var factory = new BlazorAppFactory(environment: "Development", replaceBestStoriesClient: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Startup_NonDevelopmentHost_StartsSuccessfully()
    {
        await using var factory = new BlazorAppFactory(environment: "Production", replaceBestStoriesClient: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.True(response.IsSuccessStatusCode);
    }

    private static HttpClient CreateTypedHttpClient(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IHttpClientFactory>();
        return factory.CreateClient("BestStoriesClient");
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("http://localhost:5182/api/best-stories?n=1", request.RequestUri!.ToString());
            Token = cancellationToken;
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"title":"Delayed snapshot","uri":"https://example.test/story","postedBy":"p","time":"2024-01-01T00:00:00+00:00","score":1,"commentCount":2}]""")
            };
        }
    }
}