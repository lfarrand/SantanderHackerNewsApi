using System.Net;
using System.Text;
using AwesomeAssertions;
using Bunit;
using HackerNews.BestStories.Blazor.Components.Pages;
using HackerNews.BestStories.Blazor.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNews.BestStories.Blazor.Tests.Components;

public sealed class HomeTests : BunitContext
{
    [Fact]
    public void Grid_announces_default_page_size_of_20()
    {
        const string json = """
                            [{
                              "title":"A uBlock Origin update was rejected from the Chrome Web Store",
                              "uri":"https://example.com/1",
                              "postedBy":"ismaildonmez",
                              "time":"2019-10-12T13:43:01+00:00",
                              "score":1716,
                              "commentCount":572
                            }]
                            """;
        var handler = new StaticHandler(HttpStatusCode.OK, json);
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();

        cut.Markup.Should().Contain("Default page size is 20");
        cut.FindAll("tbody tr").Count.Should().Be(1);
        cut.Markup.Should().Contain("ismaildonmez");
        cut.Find("tbody a").GetAttribute("href").Should().Be("https://example.com/1");
        cut.Find("tbody a").TextContent.Should().Be("A uBlock Origin update was rejected from the Chrome Web Store");
    }

    [Fact]
    public void Renders_empty_state_when_service_returns_no_stories()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, "[]");
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();

        cut.Markup.Should().Contain("0 stories");
        cut.Markup.Should().Contain("No stories to display.");
        cut.Markup.Should().NotContain("Loading…");
        cut.Markup.Should().Contain("Page 1 of 1");
    }

    [Fact]
    public void Renders_error_state_when_service_throws()
    {
        var handler = new StaticHandler(HttpStatusCode.BadRequest, "bad request details");
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();

        cut.Markup.Should().Contain("0 stories");
        cut.Markup.Should().Contain("bad request details");
        cut.Find("span.error").TextContent.Should().Contain("bad request details");
        cut.Markup.Should().NotContain("Loading…");
        cut.Markup.Should().Contain("No stories to display.");
    }

    [Fact]
    public void Renders_story_time_in_utc_format()
    {
        const string json = """
                            [{
                              "title":"Story",
                              "uri":"https://example.com/1",
                              "postedBy":"author",
                              "time":"2024-01-02T03:04:05+02:00",
                              "score":10,
                              "commentCount":2
                            }]
                            """;
        var handler = new StaticHandler(HttpStatusCode.OK, json);
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();

        cut.Markup.Should().Contain("2024-01-02T01:04:05+00:00");
    }

    [Fact]
    public async Task Shows_loading_then_loaded_state()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var handler = new BlockingHandler(gate, HttpStatusCode.OK, BuildStoriesJson(1));
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();

        cut.Markup.Should().Contain("Loading…");

        gate.Release();
        await cut.InvokeAsync(() => Task.CompletedTask);
        await cut.WaitForAssertionAsync(() =>
        {
            cut.Markup.Should().NotContain("Loading…");
            cut.Markup.Should().Contain("1 stories");
        });
    }

    [Fact]
    public void Supports_pager_navigation_across_multiple_pages()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, BuildStoriesJson(25));
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();

        cut.Markup.Should().Contain("Page 1 of 2");
        GetPagerButton(cut, "First").HasAttribute("disabled").Should().BeTrue();
        GetPagerButton(cut, "Previous").HasAttribute("disabled").Should().BeTrue();
        GetPagerButton(cut, "Next").HasAttribute("disabled").Should().BeFalse();
        GetPagerButton(cut, "Last").HasAttribute("disabled").Should().BeFalse();
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(1, 20));

        GetPagerButton(cut, "Next").Click();
        cut.Markup.Should().Contain("Page 2 of 2");
        GetPagerButton(cut, "Next").HasAttribute("disabled").Should().BeTrue();
        GetPagerButton(cut, "Last").HasAttribute("disabled").Should().BeTrue();
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(21, 5));

        GetPagerButton(cut, "Previous").Click();
        cut.Markup.Should().Contain("Page 1 of 2");
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(1, 20));

        GetPagerButton(cut, "Last").Click();
        cut.Markup.Should().Contain("Page 2 of 2");
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(21, 5));

        GetPagerButton(cut, "First").Click();
        cut.Markup.Should().Contain("Page 1 of 2");
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(1, 20));
    }

    [Fact]
    public void Changing_page_size_resets_to_page_1_and_updates_visible_rows()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, BuildStoriesJson(25));
        Services.AddSingleton(new BestStoriesClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://api/") },
            _ => TimeSpan.Zero));

        var cut = Render<Home>();
        GetPagerButton(cut, "Last").Click();
        cut.Markup.Should().Contain("Page 2 of 2");
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(21, 5));

        cut.Find("select").Change("10");

        cut.Markup.Should().Contain("Page 1 of 3");
        GetPagerButton(cut, "First").HasAttribute("disabled").Should().BeTrue();
        GetPagerButton(cut, "Previous").HasAttribute("disabled").Should().BeTrue();
        GetRankValues(cut).Should().BeEquivalentTo(Enumerable.Range(1, 10));
    }

    private static IReadOnlyList<int> GetRankValues(IRenderedComponent<Home> cut)
        =>
        [
            .. cut.FindAll("tbody tr td.num:first-child")
                .Select(cell => int.Parse(cell.TextContent.Trim()))
        ];

    private static AngleSharp.Dom.IElement GetPagerButton(IRenderedComponent<Home> cut, string label)
        => cut.FindAll("nav.pager button").Single(button => button.TextContent.Trim() == label);

    private static string BuildStoriesJson(int count)
    {
        var stories = Enumerable.Range(1, count)
            .Select(index =>
                $$"""
                    {
                      "title":"Story {{index}}",
                      "uri":"https://example.com/{{index}}",
                      "postedBy":"author{{index}}",
                      "time":"2024-01-01T00:00:00+00:00",
                      "score":{{1000 - index}},
                      "commentCount":{{index}}
                    }
                  """)
            .ToArray();

        return $"[{string.Join(',', stories)}]";
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class BlockingHandler(
        SemaphoreSlim gate,
        HttpStatusCode status,
        string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}