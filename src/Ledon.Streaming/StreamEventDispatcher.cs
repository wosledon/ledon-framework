using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Streaming;

/// <summary>事件分发与鉴权编排（媒体面直接驱动，不依赖外部媒体服务器回调）。</summary>
public sealed class StreamEventDispatcher
{
    private readonly IStreamAuthorizer? _authorizer;
    private readonly IStreamRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<StreamEventDispatcher> _logger;
    private readonly LedonStreamingOptions _options;

    public StreamEventDispatcher(
        IStreamRegistry registry,
        IServiceProvider services,
        ILogger<StreamEventDispatcher> logger,
        LedonStreamingOptions options,
        IStreamAuthorizer? authorizer = null)
    {
        _authorizer = authorizer;
        _registry = registry;
        _services = services;
        _logger = logger;
        _options = options;
    }

    /// <summary>鉴权（供推流/拉流入口调用）。未注册鉴权器时按 <see cref="LedonStreamingOptions.AllowWhenNoAuthorizer"/> 决定。</summary>
    public ValueTask<StreamAuthResult> AuthorizeAsync(StreamAuthContext context, CancellationToken cancellationToken = default)
    {
        if (_authorizer is not null)
            return _authorizer.AuthorizeAsync(context, cancellationToken);

        return ValueTask.FromResult(_options.AllowWhenNoAuthorizer
            ? StreamAuthResult.Allow()
            : StreamAuthResult.Deny("未配置流鉴权器（AllowWhenNoAuthorizer = false）"));
    }

    /// <summary>更新注册表并分发推/拉流事件（外部入口用）。</summary>
    public async Task DispatchAsync(
        StreamAction action,
        StreamIdentity identity,
        string? clientId,
        string? clientIp,
        string? protocol,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        var parsedProtocol = ParseProtocolName(protocol);

        switch (action)
        {
            case StreamAction.Publish:
                _registry.OnPublish(identity, clientId, clientIp, parsedProtocol, parameters);
                break;

            case StreamAction.Play:
                _registry.OnPlay(identity, clientId);
                break;
        }

        await DispatchEventAsync(
            action == StreamAction.Publish ? StreamEventType.Publish : StreamEventType.Play,
            identity,
            clientId,
            clientIp,
            protocol,
            parameters,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>只分发事件，不改注册表（媒体面已自行维护状态时用）。</summary>
    public async Task DispatchEventAsync(
        StreamEventType type,
        StreamIdentity identity,
        string? clientId,
        string? clientIp,
        string? protocol,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();

        foreach (var handler in scope.ServiceProvider.GetServices<IStreamEventHandler>())
        {
            try
            {
                await handler.HandleAsync(new StreamEvent
                {
                    Type = type,
                    Identity = identity,
                    ClientId = clientId,
                    ClientIp = clientIp,
                    Protocol = ParseProtocolName(protocol),
                    Parameters = parameters,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "流事件处理器 {Handler} 失败（{Stream} / {Type}）",
                    handler.GetType().Name, identity, type);
            }
        }
    }

    internal static StreamProtocol? ParseProtocolName(string? protocol) => protocol?.ToLowerInvariant() switch
    {
        "rtmp" => StreamProtocol.Rtmp,
        "http-flv" or "flv" => StreamProtocol.HttpFlv,
        "hls" => StreamProtocol.Hls,
        "webrtc" or "whip" or "whep" => StreamProtocol.WebRtc,
        "rtsp" => StreamProtocol.Rtsp,
        "srt" => StreamProtocol.Srt,
        _ => null,
    };
}
