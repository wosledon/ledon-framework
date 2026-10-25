using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers;

using System.Threading.Channels;
using Ledon.Streaming;
using Ledon.Streaming.Media;
using Ledon.Streaming.Playback;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// HTTP-FLV 播放端点：`GET /live/{stream}.flv` 持续输出 FLV 字节流（chunked transfer）。
/// 浏览器端配 flv.js 即可播放；首帧前自动补发 GOP 缓存，起播不等关键帧。
/// </summary>
public sealed class HttpFlvEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        builder.MapGet("/live/{app}/{name}.flv", async (
            string app,
            string name,
            HttpContext context,
            CancellationToken ct) =>
        {
            var hub = context.RequestServices.GetRequiredService<StreamHub>();
            var identity = new StreamIdentity(app, name);
            var subscription = hub.Subscribe(identity);

            if (subscription is null)
                return Results.NotFound($"流 {identity.Path} 不存在或未在发布");

            context.Response.ContentType = "video/x-flv";
            context.Response.Headers.CacheControl = "no-cache";

            // FLV 头 + 序列头由 MediaStream 的 GOP 补发机制保证（订阅时已入队）
            await using var writer = new FlvHttpWriter(context.Response.Body);

            try
            {
                await foreach (var tag in subscription.Value.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await writer.WriteAsync(tag, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 客户端断开：正常路径
            }
            finally
            {
                // 必须退订，否则观众断开后订阅者一直留在流上（内存 + 无谓的分发）
                hub.Unsubscribe(identity, subscription.Value.Id);
            }

            return Results.Empty;
        });
    }

    /// <summary>把 FLV Tag 写成 HTTP 响应流。</summary>
    private sealed class FlvHttpWriter(Stream destination) : IAsyncDisposable
    {
        private bool _headerWritten;

        public async ValueTask WriteAsync(FlvTag tag, CancellationToken cancellationToken)
        {
            var buffer = new ArrayBufferWriter<byte>();

            if (!_headerWritten)
            {
                Flv.WriteHeader(buffer);
                _headerWritten = true;
            }

            Flv.WriteTag(buffer, tag);

            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => default;
    }
}
