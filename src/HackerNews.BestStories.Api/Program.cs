using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Configuration;
using HackerNews.BestStories.Api.Errors;
using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using HackerNews.BestStories.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddOpenApi(options =>
{
    options.AddSchemaTransformer((schema, context, _) =>
    {
        if (context.JsonTypeInfo.Type == typeof(StoryDto) && schema.Properties is not null)
        {
            foreach (var name in new[] { "title", "uri", "postedBy", "time" })
            {
                if (schema.Properties[name] is OpenApiSchema property)
                {
                    property.Type = JsonSchemaType.String;
                    if (name == "time") property.Format = "date-time";
                }
            }
        }

        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        if (context.Description.HttpMethod == "GET"
            && context.Description.RelativePath?.TrimEnd('/') == "api/best-stories")
        {
            var parameter = operation.Parameters?.OfType<OpenApiParameter>()
                .SingleOrDefault(parameter => parameter.Name == "n" && parameter.In == ParameterLocation.Query);
            if (parameter?.Schema is OpenApiSchema schema)
            {
                var maxStories = context.ApplicationServices.GetRequiredService<IOptions<HackerNewsOptions>>().Value
                    .MaxStories;
                parameter.Required = true;
                parameter.Description = "Number of best stories the caller requests. Required; no server-side default.";
                parameter.Example = JsonValue.Create(1);
                schema.Type = JsonSchemaType.Integer;
                schema.Pattern = null;
                schema.Minimum = "1";
                schema.Maximum = maxStories.ToString(CultureInfo.InvariantCulture);
            }
        }

        return Task.CompletedTask;
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new UtcIso8601DateTimeOffsetConverter());
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:5288",
                "http://localhost:8081")
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
        await TypedResults.Problem(statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests", detail: "The client request limit has been reached. Retry later.")
            .ExecuteAsync(context.HttpContext);
    options.AddPolicy("best-stories", httpContext =>
    {
        var address = httpContext.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return RateLimitPartition.GetFixedWindowLimiter(
            address?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
    });
});

builder.Services
    .AddOptions<HackerNewsOptions>()
    .Bind(builder.Configuration.GetSection(HackerNewsOptions.SectionName))
    .Validate(o => o.MaxStories > 0, "MaxStories must be greater than 0.")
    .Validate(o => o.CacheTtlSeconds > 0, "CacheTtlSeconds must be greater than 0.")
    .Validate(o => o.FailureCacheTtlSeconds > 0, "FailureCacheTtlSeconds must be greater than 0.")
    .Validate(o => o.MaxConcurrency > 0, "MaxConcurrency must be greater than 0.")
    .Validate(o => o.RequestTimeoutSeconds > 0, "RequestTimeoutSeconds must be greater than 0.")
    .Validate(o => o.RefreshTimeoutSeconds > 0, "RefreshTimeoutSeconds must be greater than 0.")
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "BaseUrl must be an absolute HTTP(S) URL.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<HackerNewsOptions>>().Value);

builder.Services.AddOptions<ReverseProxyOptions>()
    .Bind(builder.Configuration.GetSection(ReverseProxyOptions.SectionName))
    .Validate(o => o.KnownProxies.All(address => IPAddress.TryParse(address, out _)),
        "ReverseProxy KnownProxies must contain valid IP addresses.")
    .Validate(o => o.KnownNetworks.All(network => System.Net.IPNetwork.TryParse(network, out var parsed) && parsed.PrefixLength > 0),
        "ReverseProxy KnownNetworks must contain bounded CIDR networks (not /0).")
    .ValidateOnStart();

builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<IOptions<ReverseProxyOptions>>((options, configured) =>
    {
        var trust = configured.Value;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var address in trust.KnownProxies) options.KnownProxies.Add(IPAddress.Parse(address));
        foreach (var network in trust.KnownNetworks) options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        options.ForwardedHeaders = trust.KnownProxies.Length + trust.KnownNetworks.Length > 0
            ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            : ForwardedHeaders.None;
    });

builder.Services
    .AddOptions<OpenApiOptions>()
    .Bind(builder.Configuration.GetSection(OpenApiOptions.SectionName));

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OpenApiOptions>>().Value);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAppCache, InMemoryAppCache>();
builder.Services.AddHttpClient<IHackerNewsClient, HackerNewsClient>((sp, httpClient) =>
{
    var options = sp.GetRequiredService<IOptions<HackerNewsOptions>>().Value;
    httpClient.BaseAddress = new Uri(options.BaseUrl);
    httpClient.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
});

builder.Services.AddSingleton<IBestStoriesService, BestStoriesService>();

var app = builder.Build();
var hasHttpsBinding = HasHttpsBinding(app.Configuration);
var openApiOptions = app.Services.GetRequiredService<IOptions<OpenApiOptions>>().Value;

if (openApiOptions.Enabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference("/swagger");
}

app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();
if (hasHttpsBinding)
{
    app.UseHttpsRedirection();
}

app.MapHealthChecks("/health");

MapBestStoryRoutes(
    app.MapGroup("/api/best-stories")
        .RequireRateLimiting("best-stories"));

app.Run();

static void MapBestStoryRoutes(RouteGroupBuilder group)
{
    group.MapGet("/", GetBestStoriesByQuery)
        .WithName("GetBestStoriesByQuery")
        .WithTags("Best Stories")
        .Produces<StoryDto[]>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status500InternalServerError)
        .ProducesProblem(StatusCodes.Status502BadGateway)
        .ProducesProblem(StatusCodes.Status504GatewayTimeout)
        .WithSummary("Get the caller-specified number of best stories.")
        .WithDescription("Query parameter 'n' is required; supply an integer within the documented range. " +
                         "Missing n returns 400 Invalid n with a required-query message and request example; " +
                         "out-of-range integers return 400 Invalid n with the configured range. No server-side default is used.");
}

static IResult MissingN(int maxStories) => TypedResults.Problem(new ProblemDetails
{
    Title = "Invalid n",
    Detail =
        $"Query parameter 'n' is required. Supply an integer between 1 and {maxStories}, for example /api/best-stories?n=1.",
    Status = StatusCodes.Status400BadRequest
});

static IResult InvalidN(int maxStories) => TypedResults.Problem(new ProblemDetails
{
    Title = "Invalid n",
    Detail = $"n must be between 1 and {maxStories}.",
    Status = StatusCodes.Status400BadRequest
});

static async Task<IResult> GetBestStoriesByQuery(
    int? n,
    IBestStoriesService stories,
    IOptions<HackerNewsOptions> options,
    CancellationToken cancellationToken)
{
    if (!n.HasValue)
    {
        return MissingN(options.Value.MaxStories);
    }

    var requested = n.Value;
    if (requested < 1 || requested > options.Value.MaxStories)
    {
        return InvalidN(options.Value.MaxStories);
    }

    var result = await stories.GetBestStoriesAsync(requested, cancellationToken);
    return TypedResults.Ok(result);
}

static bool HasHttpsBinding(IConfiguration configuration)
{
    var urls = configuration["ASPNETCORE_URLS"]
               ?? configuration["urls"]
               ?? configuration["URLS"];

    if (string.IsNullOrWhiteSpace(urls))
    {
        return false;
    }

    return urls
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}

public partial class Program;
