# 流媒体：推流 / 拉流 / 转流 / 双工

`Ledon.Streaming` 负责流媒体的**控制面**：鉴权、在线状态、事件、转流、录制、WHIP/WHEP 信令。
媒体面（转发、转码、录制）交给媒体服务器（SRS / ZLMediaKit）——框架不重复造媒体内核，
但把"接一个流媒体服务"需要的周边能力一次配齐。

## 快速搭起来

```csharp
builder.Services.AddLedonStreaming(o =>
{
    o.Secret = "<从配置中心注入>";              // 推拉流令牌签名密钥
    o.ServerApiUrl = "http://127.0.0.1:1985"; // SRS OpenAPI
    o.PublicRtmpUrl = "rtmp://media.example.com";
    o.PublicHttpUrl = "http://media.example.com";
    o.PublicWebRtcUrl = "webrtc://media.example.com/live";
});
```

配好媒体服务器的 webhook 指向 `POST /streaming/hooks`，剩下的（鉴权、状态、事件）框架接管。

## 鉴权：一把令牌管推流和拉流

```csharp
public class StreamTokenController(IStreamTokenService tokens, StreamUrlBuilder urls)
{
    public object Issue(string streamName, string userId)
    {
        var identity = StreamIdentity.Parse($"live/{streamName}");

        return new
        {
            push = urls.BuildPublishUrl(identity, extra: userId),   // OBS / ffmpeg 推流
            flv = urls.BuildFlvPlayUrl(identity, extra: userId),    // flv.js 播放
            hls = urls.BuildHlsPlayUrl(identity, extra: userId),    // Safari / 原生
            webrtc = urls.BuildWebRtcPlayUrl(identity, extra: userId),
            whip = urls.BuildWhipUrl(identity, extra: userId),      // 浏览器推流
            whep = urls.BuildWhepUrl(identity, extra: userId),      // 浏览器播放
        };
    }
}
```

- 令牌 = `签名-过期时间戳`，HMAC-SHA256：过期即失效，改一个字符验不过；
- `extra` 绑定用户/租户：令牌只对该主体有效；
- 推流令牌不能拉流、A 流的令牌不能看 B 流；
- 自定义策略：实现 `IStreamAuthorizer`（接自己的用户/付费/租户体系）。

## 播放：FLV / HLS / WebRTC 一套接口

`GET /streaming/playback/{app}/{name}` 返回这条流的全部可用协议与带令牌地址，
前端不必知道媒体服务器的地址结构：

```json
{
  "stream": "live/cam-01",
  "online": true,
  "players": 3,
  "protocols": {
    "flv": "http://media.example.com/live/cam-01.flv?token=...",
    "hls": "http://media.example.com/live/cam-01.m3u8?token=...",
    "webrtc": "webrtc://media.example.com/live/live/cam-01?token=...",
    "whep": "http://media.example.com/streaming/whep/live/cam-01?token=..."
  }
}
```

## 双工通信（WebRTC）

- **WHIP（推流）**：浏览器 `POST /streaming/whip/{app}/{name}` 带 SDP Offer → 201 + SDP Answer；
- **WHEP（拉流）**：浏览器 `POST /streaming/whep/{app}/{name}` 带 SDP Offer → 201 + SDP Answer；
- `DELETE` 结束会话；
- 鉴权在 SDP 交换之前：没有令牌连不上，媒体服务器不被打扰；
- SDP 交换由媒体服务器完成（`IMediaServerClient` 实现 `IWhilpExchange`；未实现返回 502 而不是假装成功）；
- 数据面双工（信令、聊天、白板、弹幕）用 `Ledon.SignalR`，媒体面用本包。

## 转流 / 转码 / 录制

```csharp
public class StreamOps(IMediaServerClient server)
{
    // 把上游摄像头流转到本地，再对外提供（拉流分发、汇聚）
    public Task RelayAsync(string upstream, string name) =>
        server.ConfigureRelayAsync(new StreamRelayRule(name, upstream, StreamIdentity.Parse($"live/{name}")));

    // 多路码率 / 缩放（媒体服务器执行）
    public Task TranscodeAsync(string name) =>
        server.ApplyTranscodeAsync(
            StreamIdentity.Parse($"live/{name}"),
            new TranscodeProfile("720p", "h264", Width: 1280, Height: 720, BitrateKbps: 1500));

    public Task RecordAsync(string name) => server.StartDvrAsync(StreamIdentity.Parse($"live/{name}"));
}
```

## 事件与在线状态

```csharp
public class StreamNotifier : IStreamEventHandler
{
    public Task HandleAsync(StreamEvent @event, CancellationToken ct = default)
    {
        // Publish / Unpublish / Play / Stop / DvrStarted / DvrStopped / Offline
        // @event.Identity：live/cam-01；@event.ClientIp / ClientId / Protocol
        return Task.CompletedTask;
    }
}
```

- `GET /streaming/streams`：全部在线流与观众数；
- `IStreamRegistry`：内存注册表（多实例可换 Redis 实现）；
- 媒体服务器不推"掉线"事件：`StreamOfflineSweeper` 按心跳阈值兜底标记离线。

## 边界（明确不做）

- 不内置 SFU/MCU：媒体转发交给 SRS/ZLMediaKit，框架只做控制面与信令；
- 不实现 RTMP/WebRTC 的媒体编解码：那是媒体服务器的职责；
- 录制文件管理（检索、播放、删除）由媒体服务器的存储与你的业务层负责。

完整文档：<https://github.com/wosledon/ledon-framework>
