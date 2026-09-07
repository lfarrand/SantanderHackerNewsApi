using System.Text.Json;
using HackerNews.BestStories.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HackerNews.BestStories.Api.Tests.Serialization;

public sealed class UtcIso8601DateTimeOffsetConverterTests : IAsyncDisposable
{
    private readonly ApiFactory _factory;
    private readonly JsonSerializerOptions _serializerOptions;

    public UtcIso8601DateTimeOffsetConverterTests()
    {
        _factory = new ApiFactory(new StubBestStoriesService([]));
        _serializerOptions = _factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    public void Write_EmitsInvariantUtcSeconds(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            var value = new DateTimeOffset(2019, 10, 12, 15, 43, 1, TimeSpan.FromHours(2)).AddMilliseconds(123);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, _serializerOptions));
            Assert.Equal("2019-10-12T13:43:01+00:00", json.RootElement.GetString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Read_ParsesUtcIso8601ValueWithZSuffix()
    {
        var result = DeserializeDateTimeOffset("\"2025-01-02T01:04:05.0000000Z\"");

        Assert.Equal(new DateTimeOffset(2025, 1, 2, 1, 4, 5, TimeSpan.Zero), result);
    }

    [Fact]
    public void Read_ParsesIso8601ValueWithExplicitOffset()
    {
        var result = DeserializeDateTimeOffset("\"2025-01-02T03:04:05.0000000+02:00\"");

        Assert.Equal(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)), result);
    }

    [Fact]
    public void Read_ThrowsFormatException_ForMalformedValue()
    {
        void Action() => DeserializeDateTimeOffset("\"not-a-date\"");

        Assert.Throws<FormatException>((Action)Action);
    }

    private DateTimeOffset DeserializeDateTimeOffset(string json)
        => JsonSerializer.Deserialize<DateTimeOffset>(json, _serializerOptions);
}
