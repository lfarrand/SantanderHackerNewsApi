using HackerNews.BestStories.Api.Mapping;
using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Errors;

namespace HackerNews.BestStories.Api.Tests.Mapping;

public sealed class StoryMapperTests
{
    [Theory]
    [InlineData(-62135596801L)]
    [InlineData(253402300800L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Map_InvalidTimestamp_IsAnUpstreamFailure(long seconds)
    {
        var item = new HackerNewsItem { Id = 42, Type = "story", Title = "Title", Time = seconds };
        Assert.Throws<UpstreamException>(() => StoryMapper.Map(item));
        item.Type = "comment";
        Assert.Null(StoryMapper.Map(item));
        item.Type = "story";
        item.Title = " ";
        Assert.Null(StoryMapper.Map(item));
    }

    [Theory]
    [InlineData(-62135596800L, "0001-01-01T00:00:00+00:00")]
    [InlineData(253402300799L, "9999-12-31T23:59:59+00:00")]
    public void Map_AcceptsInclusiveTimestampBounds(long seconds, string expected)
    {
        var result = StoryMapper.Map(new HackerNewsItem { Type = "story", Title = "Title", Time = seconds });
        Assert.NotNull(result);
        Assert.Equal(expected, result.Time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Map_UsesDiscussionUri_AndMissingFieldDefaults(string? url)
    {
        var result = StoryMapper.Map(new HackerNewsItem { Id = 42, Type = "STORY", Title = "Title", Url = url });
        Assert.NotNull(result);
        Assert.Equal("https://news.ycombinator.com/item?id=42", result.Url);
        Assert.Equal(string.Empty, result.PostedBy);
        Assert.Equal(0, result.CommentCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, result.Time);
        var v2 = StoryDtoV2.From(result);
        Assert.Equal(42, v2.HackerNewsId);
        Assert.Equal(result.Url, v2.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void Map_ReturnsNull_WhenTitleIsBlank(string? title)
    {
        Assert.Null(StoryMapper.Map(new HackerNewsItem { Type = "story", Title = title }));
    }

    [Fact]
    public void Map_ReturnsNull_WhenItemIsNotStory()
    {
        var item = new HackerNewsItem
        {
            Id = 1,
            Type = "comment"
        };

        var result = StoryMapper.Map(item);

        Assert.Null(result);
    }

    [Fact]
    public void Map_MapsSupportedStoryFields()
    {
        var item = new HackerNewsItem
        {
            Id = 10,
            Type = "story",
            By = "author",
            Descendants = 15,
            Score = 120,
            Time = 1_700_000_000,
            Title = "title",
            Url = "https://example.com"
        };

        var result = StoryMapper.Map(item);

        Assert.NotNull(result);
        Assert.Equal(10, result!.Id);
        Assert.Equal("author", result.PostedBy);
        Assert.Equal(15, result.CommentCount);
        Assert.Equal(120, result.Score);
        Assert.Equal("title", result.Title);
        Assert.Equal("https://example.com", result.Url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), result.Time);
    }
}