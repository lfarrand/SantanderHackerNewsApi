using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using HackerNews.BestStories.Blazor.Services;

namespace HackerNews.BestStories.Blazor.Tests.Infrastructure;

public sealed class BlazorAppFactory(
    string environment = "Development",
    string? apiBaseUrl = null,
    bool replaceBestStoriesClient = false)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);

        if (apiBaseUrl is not null)
        {
            builder.UseSetting("ApiBaseUrl", apiBaseUrl);
        }

        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            if (apiBaseUrl is null)
            {
                return;
            }

            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApiBaseUrl"] = apiBaseUrl
            });
        });

        builder.ConfigureServices(services =>
        {
            if (!replaceBestStoriesClient)
            {
                return;
            }

            services.RemoveAll<BestStoriesClient>();
            services.AddSingleton(_ =>
                new BestStoriesClient(
                    new HttpClient(new OkResponseHandler()) { BaseAddress = new Uri("http://localhost") }));
        });
    }

    private sealed class OkResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
    }
}
