using System.Net;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Tests.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HackerNews.BestStories.Api.Tests.Endpoints;

public sealed class ForwardedHeadersTests
{
    [Theory]
    [InlineData("KnownProxies", "172.30.0.10", "172.30.0.10")]
    [InlineData("KnownProxies", "172.30.0.10", "::ffff:172.30.0.10")]
    [InlineData("KnownNetworks", "172.30.0.0/24", "172.30.0.40")]
    [InlineData("KnownNetworks", "2001:db8::/64", "2001:db8::40")]
    public async Task TrustedPeer_UsesOnlyLastForwardedHop(string setting, string trusted, string peer)
    {
        await using var factory = CreateFactory(setting, trusted);
        var result = await Send(factory, peer, "198.51.100.99, 198.51.100.10", "http, https");
        Assert.Equal(200, result.Response.StatusCode);
        Assert.Equal(IPAddress.Parse("198.51.100.10"), result.Connection.RemoteIpAddress);
        Assert.Equal("https", result.Request.Scheme);
        Assert.Equal("198.51.100.99", result.Request.Headers["X-Forwarded-For"].ToString());
    }

    [Theory]
    [InlineData("172.30.0.10", "203.0.113.9")]
    [InlineData(null, "127.0.0.1")]
    public async Task UntrustedOrUnconfiguredPeer_IgnoresForgedHeaders(string? trusted, string peer)
    {
        await using var factory = CreateFactory("KnownProxies", trusted);
        var result = await Send(factory, peer, "198.51.100.10");
        Assert.Equal(200, result.Response.StatusCode);
        Assert.Equal(IPAddress.Parse(peer), result.Connection.RemoteIpAddress);
        Assert.Equal("http", result.Request.Scheme);
    }

    [Fact]
    public async Task TrustedChain_StillConsumesOnlyOneHop()
    {
        await using var factory = CreateFactory("KnownNetworks", "172.30.0.0/24");
        var result = await Send(factory, "172.30.0.10", "198.51.100.99, 172.30.0.20", "http, https");
        Assert.Equal(IPAddress.Parse("172.30.0.20"), result.Connection.RemoteIpAddress);
        Assert.Equal("https", result.Request.Scheme);
        Assert.Equal("198.51.100.99", result.Request.Headers["X-Forwarded-For"].ToString());
    }

    [Fact]
    public async Task OutsideTrustedNetwork_IgnoresHeaders()
    {
        await using var factory = CreateFactory("KnownNetworks", "172.30.0.0/24");
        var result = await Send(factory, "172.30.1.10", "198.51.100.10");
        Assert.Equal(IPAddress.Parse("172.30.1.10"), result.Connection.RemoteIpAddress);
        Assert.Equal("http", result.Request.Scheme);
    }

    [Fact]
    public async Task TrustedProto_IsAppliedBeforeHttpsRedirection()
    {
        await using var factory = CreateFactory("KnownProxies", "172.30.0.10")
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["urls"] = "https://localhost:7443",
                    ["https_port"] = "7443"
                })));
        var secure = await Send(factory, "172.30.0.10", "198.51.100.10");
        Assert.Equal(200, secure.Response.StatusCode);
        var insecure = await Send(factory, "172.30.0.10", "198.51.100.10", "http");
        Assert.Equal(307, insecure.Response.StatusCode);
        Assert.Equal("https://localhost:7443/api/best-stories?n=1", insecure.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task TrustedClients_HaveSeparateRateLimitPartitions()
    {
        var upstream = new CompleteSnapshotTests.Upstream();
        await using var factory = CreateFactory("KnownProxies", "172.30.0.10", upstream);
        for (var request = 0; request < 60; request++)
            Assert.Equal(200, (await Send(factory, "172.30.0.10", "198.51.100.10")).Response.StatusCode);
        Assert.Equal(429, (await Send(factory, "172.30.0.10", "198.51.100.10")).Response.StatusCode);
        for (var request = 0; request < 60; request++)
            Assert.Equal(200, (await Send(factory, "172.30.0.10", "198.51.100.11")).Response.StatusCode);
        Assert.Equal(429, (await Send(factory, "172.30.0.10", "198.51.100.11")).Response.StatusCode);
        Assert.Equal(1, upstream.IdCalls);
    }

    [Fact]
    public async Task UntrustedClients_CannotEvadeRateLimit()
    {
        await using var factory = CreateFactory("KnownProxies", "172.30.0.10");
        for (var request = 0; request < 60; request++)
            Assert.Equal(200, (await Send(factory, "203.0.113.9", "198.51.100.10")).Response.StatusCode);
        Assert.Equal(429, (await Send(factory, "203.0.113.9", "198.51.100.11")).Response.StatusCode);
        Assert.Equal(429, (await Send(factory, "203.0.113.9", "198.51.100.12")).Response.StatusCode);
    }

    [Theory]
    [InlineData("::ffff:198.51.100.10", "198.51.100.10")]
    [InlineData("198.51.100.10", "::ffff:198.51.100.10")]
    [InlineData("2001:db8::c633:640a", "2001:db8:0:0:0:0:c633:640a")]
    public async Task EquivalentDirectAndForwardedAddresses_ShareRateLimitPartition(string direct, string forwarded)
    {
        var upstream = new CompleteSnapshotTests.Upstream();
        await using var factory = CreateFactory("KnownProxies", "172.30.0.10", upstream);
        for (var request = 0; request < 60; request++)
        {
            var result = request % 2 == 0
                ? await Send(factory, direct, "203.0.113.99")
                : await Send(factory, "172.30.0.10", forwarded);
            Assert.Equal(200, result.Response.StatusCode);
        }
        Assert.Equal(429, (await Send(factory, direct, "203.0.113.100")).Response.StatusCode);
        Assert.Equal(429, (await Send(factory, "172.30.0.10", forwarded)).Response.StatusCode);
        Assert.Equal(200, (await Send(factory, "172.30.0.10", "198.51.100.11")).Response.StatusCode);
        // A native IPv6 address is not an IPv4 alias merely because the last 32 bits match.
        Assert.Equal(200, (await Send(factory, "172.30.0.10",
            direct.Contains("2001:") ? "198.51.100.10" : "2001:db8::c633:640a")).Response.StatusCode);
        Assert.Equal(1, upstream.IdCalls);
    }

    [Theory]
    [InlineData("KnownProxies", "not-an-address")]
    [InlineData("KnownNetworks", "invalid/24")]
    [InlineData("KnownNetworks", "0.0.0.0/0")]
    [InlineData("KnownNetworks", "::/0")]
    public void Startup_RejectsInvalidProxyTrust(string setting, string trusted)
    {
        using var factory = CreateFactory(setting, trusted);
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static WebApplicationFactory<Program> CreateFactory(string setting, string? trusted,
        CompleteSnapshotTests.Upstream? upstream = null) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            if (trusted is not null)
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { [$"ReverseProxy:{setting}:0"] = trusted }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHackerNewsClient>();
                services.AddSingleton<IHackerNewsClient>(upstream ?? new CompleteSnapshotTests.Upstream());
            });
        });

    private static Task<HttpContext> Send(WebApplicationFactory<Program> factory, string peer,
        string forwarded, string proto = "https") => factory.Server.SendAsync(context =>
    {
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/api/best-stories";
        context.Request.QueryString = new QueryString("?n=1");
        context.Request.Headers["X-Forwarded-For"] = forwarded;
        context.Request.Headers["X-Forwarded-Proto"] = proto;
        context.Request.Headers["X-Real-IP"] = "192.0.2.99";
    });
}