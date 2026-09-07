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

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void Read_ParsesInvariantValues_PreservingInstantAndOffset(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            (string Text, int OffsetMinutes, long FractionTicks)[] cases =
            [
                ("2025-01-02T03:04:05Z", 0, 0),
                ("2025-01-02T03:04:05+02:00", 120, 0),
                ("2025-01-02T03:04:05.1234567Z", 0, 1234567),
                ("2025-01-02T03:04:05.1234567+02:00", 120, 1234567),
                ("2025-01-02T03:04:05.123-05:30", -330, 1230000)
            ];

            foreach (var (text, offsetMinutes, fractionTicks) in cases)
            {
                var expected = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.FromMinutes(offsetMinutes))
                    .AddTicks(fractionTicks);
                var result = DeserializeDateTimeOffset(JsonSerializer.Serialize(text));

                Assert.Equal(expected, result);
                Assert.Equal(expected.Offset, result.Offset);
            }
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void Read_ThrowsFormatException_ForMalformedValue(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
            Assert.Throws<FormatException>(() =>
            {
                DeserializeDateTimeOffset("\"not-a-date\"");
            });
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    private DateTimeOffset DeserializeDateTimeOffset(string json)
        => JsonSerializer.Deserialize<DateTimeOffset>(json, _serializerOptions);
}
