using HackerNews.BestStories.Blazor.Components;
using HackerNews.BestStories.Blazor.Configuration;
using HackerNews.BestStories.Blazor.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5182/";
if (!apiBase.EndsWith('/'))
{
    apiBase += "/";
}

builder.Services.AddOptions<ApiClientOptions>()
    .Bind(builder.Configuration.GetSection(ApiClientOptions.SectionName))
    .Validate(options => options.RefreshTimeoutSeconds > 0,
        "ApiClient RefreshTimeoutSeconds must be positive and match the API refresh deadline.")
    .Validate(options => options.RequestTimeoutSeconds > options.RefreshTimeoutSeconds
                         && options.RequestTimeoutSeconds <= int.MaxValue / 1000,
        "ApiClient RequestTimeoutSeconds must exceed RefreshTimeoutSeconds and fit HttpClient's timeout range.")
    .ValidateOnStart();

builder.Services.AddHttpClient<BestStoriesClient>((services, client) =>
{
    client.BaseAddress = new Uri(apiBase);
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<IOptions<ApiClientOptions>>().Value.RequestTimeoutSeconds);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
