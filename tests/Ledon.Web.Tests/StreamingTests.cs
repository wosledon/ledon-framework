using Ledon.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>流媒体：令牌鉴权、注册表、地址构建、事件分发。</summary>
public class StreamingTests
{
    private static LedonStreamingOptions Options() => new()
    {
        Secret = "test-secret",
        PublicRtmpUrl = "rtmp://media.example.com",
        PublicHttpUrl = "http://media.example.com",
    };

    private static IStreamTokenService Tokens(LedonStreamingOptions? options = null) =>
        new HmacStreamTokenService(options ?? Options());

    [Fact]
    public void Token_validates_for_same_identity_and_action()
    {
        var tokens = Tokens();
        var identity = StreamIdentity.Parse("live/cam-01");

        var token = tokens.CreateToken(identity, StreamAction.Publish);

        Assert.True(tokens.Validate(identity, StreamAction.Publish, token.Value));
    }

    [Fact]
    public void Token_rejects_different_action()
    {
        var tokens = Tokens();
        var identity = StreamIdentity.Parse("live/cam-01");

        var publishToken = tokens.CreateToken(identity, StreamAction.Publish);

        // 推流令牌不能拿来拉流
        Assert.False(tokens.Validate(identity, StreamAction.Play, publishToken.Value));
    }

    [Fact]
    public void Token_rejects_different_stream()
    {
        var tokens = Tokens();
        var token = tokens.CreateToken(StreamIdentity.Parse("live/cam-01"), StreamAction.Play);

        Assert.False(tokens.Validate(StreamIdentity.Parse("live/cam-02"), StreamAction.Play, token.Value));
    }

    [Fact]
    public void Token_rejects_tampering()
    {
        var tokens = Tokens();
        var identity = StreamIdentity.Parse("live/cam-01");
        var token = tokens.CreateToken(identity, StreamAction.Play);

        var tampered = $"{token.Value[..^2]}xx";

        Assert.False(tokens.Validate(identity, StreamAction.Play, tampered));
    }

    [Fact]
    public void Token_expires()
    {
        var tokens = Tokens();
        var identity = StreamIdentity.Parse("live/cam-01");

        var token = tokens.CreateToken(identity, StreamAction.Play, TimeSpan.FromSeconds(-1));

        Assert.False(tokens.Validate(identity, StreamAction.Play, token.Value));
    }

    [Fact]
    public void Token_binds_extra_subject()
    {
        var tokens = Tokens();
        var identity = StreamIdentity.Parse("live/cam-01");

        var forAlice = tokens.CreateToken(identity, StreamAction.Play, extra: "alice");

        Assert.True(tokens.Validate(identity, StreamAction.Play, forAlice.Value, "alice"));
        Assert.False(tokens.Validate(identity, StreamAction.Play, forAlice.Value, "bob"));
        Assert.False(tokens.Validate(identity, StreamAction.Play, forAlice.Value));
    }

    [Fact]
    public void Empty_token_is_rejected()
    {
        var tokens = Tokens();
        var identity = StreamIdentity.Parse("live/cam-01");

        Assert.False(tokens.Validate(identity, StreamAction.Play, null));
        Assert.False(tokens.Validate(identity, StreamAction.Play, ""));
        Assert.False(tokens.Validate(identity, StreamAction.Play, "garbage"));
    }

    [Fact]
    public void Identity_parses_path_and_rejects_garbage()
    {
        var identity = StreamIdentity.Parse("live/cam-01");

        Assert.Equal("live", identity.App);
        Assert.Equal("cam-01", identity.Name);
        Assert.Equal("live/cam-01", identity.Path);
        Assert.Throws<ArgumentException>(() => StreamIdentity.Parse("no-slash"));
    }

    [Fact]
    public void Urls_carry_token_and_protocol()
    {
        var options = Options();
        var builder = new StreamUrlBuilder(Tokens(options), options);
        var identity = StreamIdentity.Parse("live/cam-01");

        var flv = builder.BuildFlvPlayUrl(identity);
        var rtmp = builder.BuildPublishUrl(identity);
        var hls = builder.BuildHlsPlayUrl(identity);

        Assert.StartsWith("http://media.example.com/live/cam-01.flv?token=", flv);
        Assert.StartsWith("rtmp://media.example.com/live/cam-01?token=", rtmp);
        Assert.StartsWith("http://media.example.com/live/cam-01.m3u8?token=", hls);

        // 每个地址带的令牌都能验过
        Assert.True(Tokens(options).Validate(identity, StreamAction.Play, TokenFrom(flv)));
        Assert.True(Tokens(options).Validate(identity, StreamAction.Publish, TokenFrom(rtmp)));
        Assert.True(Tokens(options).Validate(identity, StreamAction.Play, TokenFrom(hls)));
    }

    [Fact]
    public void Registry_tracks_publish_play_and_stop()
    {
        var registry = new InMemoryStreamRegistry(Options(), Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryStreamRegistry>.Instance);
        var identity = StreamIdentity.Parse("live/cam-01");

        registry.OnPublish(identity, "pub-1", "10.0.0.1", StreamProtocol.Rtmp, null);
        registry.OnPlay(identity, "view-1");
        registry.OnPlay(identity, "view-2");

        var status = registry.Find(identity);
        Assert.NotNull(status);
        Assert.True(status!.Online);
        Assert.Equal(2, status.PlayerCount);
        Assert.Equal("pub-1", status.PublisherId);
        Assert.Single(registry.Snapshot());   // 一条流（两个观众）

        registry.OnStop(identity, "view-1", isPublisher: false);
        Assert.Equal(1, registry.PlayerCount(identity));

        registry.OnStop(identity, "pub-1", isPublisher: true);
        status = registry.Find(identity);
        Assert.False(status!.Online);
        Assert.Equal(0, status.PlayerCount);
    }

    [Fact]
    public async Task Registry_sweeps_stale_streams_offline()
    {
        var options = Options();
        options.OfflineThreshold = TimeSpan.FromMilliseconds(1);
        var registry = new InMemoryStreamRegistry(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryStreamRegistry>.Instance);
        var identity = StreamIdentity.Parse("live/cam-01");

        registry.OnPublish(identity, "pub-1", null, null, null);
        await Task.Delay(20);   // 超过 1ms 的心跳阈值

        Assert.Equal(1, registry.SweepOffline());
        Assert.False(registry.Find(identity)!.Online);
    }

    [Fact]
    public async Task Playback_endpoint_reports_protocols_and_status()
    {
        var app = BuildApp(new StreamRecorder());
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<IStreamRegistry>();
        registry.OnPublish(StreamIdentity.Parse("live/cam-01"), "pub-1", null, StreamProtocol.Rtmp, null);
        registry.OnPlay(StreamIdentity.Parse("live/cam-01"), "view-1");

        var client = app.GetTestClient();
        var response = await client.GetAsync("/streaming/playback/live/cam-01");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"online\":true", json);
        Assert.Contains("\"players\":1", json);
        Assert.Contains("flv", json);
        Assert.Contains("hls", json);

        await app.StopAsync();
    }

    [Fact]
    public async Task Streams_endpoint_lists_live_streams()
    {
        var app = BuildApp(new StreamRecorder());
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<IStreamRegistry>();
        registry.OnPublish(StreamIdentity.Parse("live/cam-01"), "pub-1", null, StreamProtocol.Rtmp, null);

        var client = app.GetTestClient();
        var response = await client.GetAsync("/streaming/streams");

        response.EnsureSuccessStatusCode();
        Assert.Contains("live/cam-01", await response.Content.ReadAsStringAsync());

        await app.StopAsync();
    }

    private static WebApplication BuildApp(params IStreamEventHandler[] handlers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonStreaming(o =>
        {
            o.Secret = "test-secret";
            o.PublicRtmpUrl = "rtmp://media.example.com";
            o.PublicHttpUrl = "http://media.example.com";
        });

        foreach (var handler in handlers)
            builder.Services.AddSingleton(handler);

        var app = builder.Build();
        app.UseLedonWeb();

        return app;
    }

    private static string TokenFrom(string url) =>
        new Uri(url).Query.TrimStart('?').Split('=', 2)[1];

    private static Microsoft.Extensions.Logging.ILogger<T> NullLogger<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    [Fact]
    public async Task Missing_authorizer_follows_allow_when_no_authorizer()
    {
        var options = Options();
        var registry = new InMemoryStreamRegistry(options, NullLogger<InMemoryStreamRegistry>());
        var services = new ServiceCollection().BuildServiceProvider();

        var dispatcher = new StreamEventDispatcher(
            registry,
            services,
            NullLogger<StreamEventDispatcher>(),
            options);

        var context = new StreamAuthContext
        {
            Identity = StreamIdentity.Parse("live/cam-01"),
            Action = StreamAction.Publish,
        };

        // 没注册鉴权器：默认 fail-closed
        Assert.False((await dispatcher.AuthorizeAsync(context)).Allowed);

        options.AllowWhenNoAuthorizer = true;
        Assert.True((await dispatcher.AuthorizeAsync(context)).Allowed);
    }

    private sealed class StreamRecorder : IStreamEventHandler
    {
        public string? LastStream { get; private set; }
        public StreamEventType? LastType { get; private set; }

        public Task HandleAsync(StreamEvent @event, CancellationToken cancellationToken = default)
        {
            LastStream = @event.Identity.Path;
            LastType = @event.Type;
            return Task.CompletedTask;
        }
    }
}
