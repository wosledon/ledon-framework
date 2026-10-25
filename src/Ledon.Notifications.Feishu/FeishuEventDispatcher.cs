using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Ledon.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledon.Notifications.Feishu;

/// <summary>事件回调分发器：URL 校验、解密、Token 校验、按事件类型分发。</summary>
public sealed class FeishuEventDispatcher
{
    private readonly FeishuOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<FeishuEventDispatcher> _logger;

    public FeishuEventDispatcher(FeishuOptions options, IServiceProvider services, ILogger<FeishuEventDispatcher> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    /// <summary>处理一次回调；返回要写回的响应体。</summary>
    public async Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        string raw;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw))
            return Results.Text("{}", "application/json");

        // 1) 解密（开启 EncryptKey 时飞书把整个请求体包在 encrypt 字段里）
        if (!string.IsNullOrWhiteSpace(_options.EncryptKey))
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("encrypt", out var encrypted))
            {
                var plain = FeishuCrypto.DecryptEvent(_options.EncryptKey, encrypted.GetString() ?? "");
                raw = plain;
            }
        }

        using var payload = JsonDocument.Parse(raw);
        var root = payload.RootElement;

        // 2) URL 校验（配置回调地址时飞书会先推一次 challenge）
        if (root.TryGetProperty("type", out var type) && type.GetString() == "url_verification")
        {
            var challenge = root.GetProperty("challenge").GetString() ?? "";
            var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;

            if (!TokenMatches(token))
                return Results.Unauthorized();

            _logger.LogInformation("飞书 URL 校验通过");
            return Results.Json(new { challenge });
        }

        // 3) Token 校验（防止伪造回调）
        var headerToken = root.TryGetProperty("header", out var header) && header.TryGetProperty("token", out var ht)
            ? ht.GetString()
            : root.TryGetProperty("token", out var rt) ? rt.GetString() : null;

        if (!TokenMatches(headerToken))
        {
            _logger.LogWarning("飞书回调 Token 校验失败（已拒绝）");
            return Results.Unauthorized();
        }

        // 4) 反序列化并分发
        var @event = JsonSerializer.Deserialize<FeishuEvent>(raw, FeishuJson.Options);
        if (@event is null)
            return Results.Text("{}", "application/json");

        // 飞书要求 200 才算投递成功：处理器异常只记录，不影响响应
        using var scope = _services.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IFeishuEventHandler>();

        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(@event, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "飞书事件处理器 {Handler} 失败（事件 {EventType}）",
                    handler.GetType().Name, @event.EventType);
            }
        }

        _logger.LogDebug("飞书事件 {EventType} 已分发（{Count} 个处理器）", @event.EventType, handlers.Count());

        return Results.Text("{}", "application/json");
    }

    private bool TokenMatches(string? token)
    {
        if (string.IsNullOrWhiteSpace(_options.VerificationToken))
            return true;   // 未开启校验

        return string.Equals(_options.VerificationToken, token, StringComparison.Ordinal);
    }
}

/// <summary>事件回调端点（<see cref="IEndpoint"/> 实现会被自动映射）。</summary>
public sealed class FeishuEventEndpoint(FeishuOptions options) : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        builder.MapPost(options.EventRoute, async (HttpContext context, FeishuEventDispatcher dispatcher, CancellationToken ct) =>
            await dispatcher.HandleAsync(context, ct).ConfigureAwait(false));
    }
}

/// <summary>JSON 序列化选项（事件体原样保留，字段命名按下划线）。</summary>
internal static class FeishuJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
