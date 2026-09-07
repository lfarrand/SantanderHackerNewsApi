using System.Text.Json;
using System.Text.Json.Serialization;

namespace HackerNews.BestStories.Api;

internal sealed class UtcIso8601DateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private static readonly string[] Iso8601Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString()!;
        if (!DateTimeOffset.TryParseExact(
                value,
                Iso8601Formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var result))
        {
            throw new FormatException($"The value '{value}' is not a valid ISO-8601 date/time offset.");
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
}