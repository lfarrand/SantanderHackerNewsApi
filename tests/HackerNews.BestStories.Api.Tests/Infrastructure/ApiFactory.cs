using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HackerNews.BestStories.Api.Tests.Infrastructure;

public sealed class ApiFactory(IBestStoriesService service) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBestStoriesService>();
            services.AddSingleton(service);
        });
    }
}

public sealed class StubBestStoriesService(IReadOnlyList<StoryDto> stories) : IBestStoriesService
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<StoryDto>> GetBestStoriesAsync(int n, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<StoryDto>>([.. stories.Take(n)]);
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ThrowingBestStoriesService(Exception exceptionToThrow) : IBestStoriesService
{
    public Task<IReadOnlyList<StoryDto>> GetBestStoriesAsync(int n, CancellationToken cancellationToken)
        => Task.FromException<IReadOnlyList<StoryDto>>(exceptionToThrow);

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}