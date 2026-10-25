# Ledon.Streaming

流媒体控制面：推流/拉流鉴权、在线状态、事件、转流、录制、WHIP/WHEP 双工信令。
媒体面（转发/转码/录制）交给媒体服务器（SRS / ZLMediaKit）——框架不重复造媒体内核。

```csharp
builder.Services.AddLedonStreaming(o =>
{
    o.Secret = "<从配置中心注入>";              // 令牌签名密钥
    o.ServerApiUrl = "http://127.0.0.1:1985"; // SRS OpenAPI
    o.PublicRtmpUrl = "rtmp://media.example.com";
    o.PublicHttpUrl = "http://media.example.com";
    o.PublicWebRtcUrl = "webrtc://media.example.com/live";
});
```

## 鉴权（推流/拉流一把令牌）

```csharp
public class StreamTokenController(IStreamTokenService tokens, StreamUrlBuilder urls)
{
    public object Issue(string streamName, string userId)
    {
        var identity = StreamIdentity.Parse($"live/{streamName}");

        return new
        {
            push = urls.BuildPublishUrl(identity, extra: userId),   // OBS/ffmpeg 用
            flv = urls.BuildFlvPlayUrl(identity, extra: userId),    // flv.js
            hls = urls.BuildHlsPlayUrl(identity, extra: userId),    // Safari/原生
            webrtc = urls.BuildWebRtcPlayUrl(identity, extra: userId),
            whip = urls.BuildWhipUrl(identity, extra: userId),      // 浏览器推流
            whep = urls.BuildWhepUrl(identity, extra: userId),      // 浏览器播放
        };
    }
}
```

- 令牌 = `签名-过期时间戳`，HMAC-SHA256；过期即失效，改一个字符验不过；
- `extra` 绑定用户/租户：令牌只对该主体有效（换个人用不了）；
- 自定义策略：实现 `IStreamAuthorizer`（接自己的用户/付费/租户体系）。

## 事件（上线/下线/开播/观看）

```csharp
public class StreamNotifier : IStreamEventHandler
{
    public Task HandleAsync(StreamEvent @event, CancellationToken ct = default)
    {
        // @event.Type: Publish / Unpublish / Play / Stop / DvrStarted / DvrStopped / Offline
        // @event.Identity：live/camera-01
        return Task.CompletedTask;
    }
}
```

媒体服务器把 on_publish / on_play 等回调配到 `POST /streaming/hooks`：
鉴权通过返回 `{"code":0}`，拒绝返回 `{"code":1}`（媒体服务器随即断开该客户端）。

## 在线状态与运维

- `GET /streaming/streams`：全部在线流与观众数；
- `GET /streaming/playback/{app}/{name}`：这条流能用什么协议看（带令牌的地址）；
- `IStreamRegistry`：内存注册表（多实例可换 Redis 实现）；
- `IMediaServerClient`：踢下线、开始/停止录制、配置转流（上游 → 本地）、应用转码 Profile。

## 转流 / 转码

```csharp
// 把上游摄像头流转到本地，再对外提供（拉流分发）
await mediaServer.ConfigureRelayAsync(
    new StreamRelayRule("cam-01", "rtsp://192.168.1.10/stream1", StreamIdentity.Parse("live/cam-01")));

// 多路码率/缩放（由媒体服务器执行）
await mediaServer.ApplyTranscodeAsync(
    StreamIdentity.Parse("live/cam-01"),
    new TranscodeProfile("720p", "h264", Width: 1280, Height: 720, BitrateKbps: 1500));
```

## 双工通信（WebRTC）

- WHIP（推流）：浏览器 `POST /streaming/whip/{app}/{name}` 带 SDP Offer → 201 + SDP Answer；
- WHEP（拉流）：浏览器 `POST /streaming/whep/{app}/{name}` 带 SDP Offer → 201 + SDP Answer；
- DELETE 结束会话；
- SDP 交换由媒体服务器完成（`IMediaServerClient` 实现 `IWhilpExchange` 时可用，否则返回 502）；
- 数据面双工（信令、聊天、白板）用 `Ledon.SignalR`，媒体面用本包。

完整文档：<https://github.com/wosledon/ledon-framework>
