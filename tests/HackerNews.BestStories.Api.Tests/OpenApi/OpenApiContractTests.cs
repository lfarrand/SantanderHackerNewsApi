using System.Text.Json;
using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace HackerNews.BestStories.Api.Tests.OpenApi;

public sealed class OpenApiContractTests
{
    [Theory]
    [InlineData(null, 500)]
    [InlineData(3, 3)]
    public async Task OpenApiDocument_DescribesRequiredCount_WithConfiguredBounds(int? configuredMaximum,
        int expectedMaximum)
    {
        await using var factory = new ApiFactory(new StubBestStoriesService([]));
        await using var configuredFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                if (configuredMaximum.HasValue)
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["HackerNews:MaxStories"] = configuredMaximum.Value.ToString()
                    });
                }
            }));
        using var client = configuredFactory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var operation = document.RootElement.GetProperty("paths").GetProperty("/api/best-stories").GetProperty("get");
        var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "n");

        AssertCountMetadata(parameter, expectedMaximum);
        Assert.Equal("Get the caller-specified number of best stories.", operation.GetProperty("summary").GetString());
        Assert.Contains("required", operation.GetProperty("description").GetString());
        Assert.Contains("Missing", operation.GetProperty("description").GetString());
        Assert.Contains("out-of-range", operation.GetProperty("description").GetString());

        if (!configuredMaximum.HasValue)
        {
            var publishedPath = Path.Combine(AppContext.BaseDirectory, "OpenApi", "published-v1.json");
            using var published = JsonDocument.Parse(await File.ReadAllTextAsync(publishedPath));
            var publishedParameter = Assert.Single(published.RootElement.GetProperty("paths")
                .GetProperty("/api/best-stories").GetProperty("get").GetProperty("parameters").EnumerateArray());
            Assert.Equal("n", publishedParameter.GetProperty("name").GetString());
            AssertCountMetadata(publishedParameter, expectedMaximum);
        }
    }

    private static void AssertCountMetadata(JsonElement parameter, int maximum)
    {
        Assert.Equal("query", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        Assert.Equal("Number of best stories the caller requests. Required; no server-side default.",
            parameter.GetProperty("description").GetString());
        Assert.Equal(1, parameter.GetProperty("example").GetInt32());
        Assert.False(parameter.TryGetProperty("default", out _));
        var schema = parameter.GetProperty("schema");
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.Equal(1, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(maximum, schema.GetProperty("maximum").GetInt32());
        Assert.False(schema.TryGetProperty("default", out _));
    }

    [Fact]
    public async Task OpenApiDocument_ContainsExpectedBaselinePathsAndSchema()
    {
        await using var factory = new ApiFactory(new StubBestStoriesService([
            new StoryDto
            {
                Id = 7,
                Title = "Story",
                Time = new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)
            }
        ]));
        using var client = factory.CreateClient();

        var openApiJson = await client.GetStringAsync("/openapi/v1.json");
        using var openApiDoc = JsonDocument.Parse(openApiJson);

        var baselinePath = Path.Combine(AppContext.BaseDirectory, "OpenApi", "openapi-v1-baseline.json");
        using var baselineDoc = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));

        foreach (var expectedPath in baselineDoc.RootElement.GetProperty("paths").EnumerateArray()
                     .Select(x => x.GetString()))
        {
            Assert.True(
                openApiDoc.RootElement.GetProperty("paths").TryGetProperty(expectedPath!, out _),
                $"Path '{expectedPath}' was not found in OpenAPI document.");
        }

        var pathParameterNames = baselineDoc.RootElement.GetProperty("pathParameterNames");
        foreach (var pathEntry in pathParameterNames.EnumerateObject())
        {
            var operation = openApiDoc.RootElement
                .GetProperty("paths")
                .GetProperty(pathEntry.Name)
                .GetProperty("get");
            var parametersByName = operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!)
                : [];

            foreach (var expectedParameter in pathEntry.Value.EnumerateArray().Select(x => x.GetString()))
            {
                Assert.True(parametersByName.TryGetValue(expectedParameter!, out var parameter),
                    $"Parameter '{expectedParameter}' was not found for path '{pathEntry.Name}'.");
                Assert.Equal("query", parameter.GetProperty("in").GetString());
            }
        }

        AssertStorySchema(openApiDoc.RootElement);
        using var published =
            JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "OpenApi", "published-v1.json")));
        AssertStorySchema(published.RootElement);
        Assert.Equal(
            baselineDoc.RootElement.GetProperty("requiredStoryProperties").EnumerateArray().Select(p => p.GetString())
                .Order(),
            openApiDoc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("StoryDto")
                .GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order());
        foreach (var section in new[] { "paths", "components" })
        {
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(published.RootElement.GetProperty(section).GetRawText()),
                    System.Text.Json.Nodes.JsonNode.Parse(openApiDoc.RootElement.GetProperty(section).GetRawText())),
                $"Published {section} differs from the live document.");
        }

        var responses = openApiDoc.RootElement.GetProperty("paths").GetProperty("/api/best-stories").GetProperty("get")
            .GetProperty("responses");
        Assert.Equal(new[] { "200", "400", "429", "500", "502", "504" },
            responses.EnumerateObject().Select(p => p.Name).Order());
        var success = responses.GetProperty("200").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("array", success.GetProperty("type").GetString());
        Assert.Equal("#/components/schemas/StoryDto", success.GetProperty("items").GetProperty("$ref").GetString());
        foreach (var status in new[] { "400", "429", "500", "502", "504" })
        {
            Assert.Equal("#/components/schemas/ProblemDetails", responses.GetProperty(status).GetProperty("content")
                .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString());
        }

        var exportPath = Environment.GetEnvironmentVariable("OPENAPI_EXPORT_PATH");
        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(exportPath))!);
            await File.WriteAllTextAsync(exportPath, openApiJson);
        }
    }

    private static void AssertStorySchema(JsonElement document)
    {
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("StoryDto");
        var fields = new[] { "commentCount", "postedBy", "score", "time", "title", "uri" };
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(fields, schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(fields, schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()).Order());
        foreach (var field in fields)
        {
            Assert.True(
                schema.GetProperty("properties").GetProperty(field).GetProperty("type").ValueKind ==
                JsonValueKind.String,
                $"Unexpected schema for {field}: {schema.GetRawText()}");
            Assert.Equal(field is "score" or "commentCount" ? "integer" : "string",
                schema.GetProperty("properties").GetProperty(field).GetProperty("type").GetString());
        }

        Assert.Equal("date-time",
            schema.GetProperty("properties").GetProperty("time").GetProperty("format").GetString());
    }
}