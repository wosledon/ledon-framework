# 流媒体：推流 / 拉流 / 转流 / 双工

`Ledon.Streaming` 分两层，**媒体面是真的协议栈，不是壳**：

| 层 | 内容 | 位置 |
| --- | --- | --- |
| **媒体面** | RTMP 服务端（握手 / 分块流 / 命令 / 媒体转发）、HTTP-FLV 播放、HLS 实时切片、FLV 封装、AMF0 编解码、TS 复用 | `Rtmp/` `Playback/` `Hls/` `Media/` |
| **控制面** | 推拉流令牌鉴权、播放地址协商、在线状态、事件、转流/录制控制、WHIP/WHEP 信令 | 包根目录 |

媒体面直接在进程内跑：OBS / ffmpeg 推 `rtmp://host:1935/live/name`，
观众用 `http://host/live/name.flv`（flv.js）或 `http://host/live/name.m3u8`（Safari）即可播放。
控制面负责把"接一个外部媒体服务器"（SRS / ZLMediaKit）需要的周边能力一次配齐。

## 快速搭起来

```csharp
builder.Services.AddLedonStreaming(o =>
{
    o.Secret = "<从配置中心注入>";              // 推拉流令牌签名密钥
    o.ServerApiUrl = "http://127.0.0.1:1985"; // SRS OpenAPI（控制面用）
    o.PublicRtmpUrl = "rtmp://media.example.com";
    o.PublicHttpUrl = "http://media.example.com";
    o.PublicWebRtcUrl = "webrtc://media.example.com/live";
});

// 媒体面：RTMP 监听（默认 1935），可按配置中心覆盖
builder.Services.Configure<RtmpServerOptions>(o =>
{
    o.Address = "0.0.0.0";
    o.Port = 1935;
    o.OutgoingChunkSize = 4096;      // 我们发给对端的块大小
});
```

`AddLedonStreaming` 会一并注册 `RtmpServer` / `HlsSegmenter` / `StreamHubSweeper`
（都是 `IHostedService`），宿主启动即监听；HTTP-FLV / HLS 端点随 `UseLedonWeb()` 自动映射。

## 媒体面：RTMP 推流 / 拉流

`RtmpServer` 是完整的 RTMP 协议栈（不是转发壳）：

- **握手**：C0/C1/C2 ↔ S0/S1/S2（1537 + 1536 字节），简单模式下回显对端随机数；
- **分块流**：fmt 0/1/2/3 全格式解码、按 chunk size 重组、扩展时间戳（≥ 0xFFFFFF）、
  重组状态按 chunk stream id 隔离（规范允许不同 csid 的 Chunk 交错）；
- **命令**：`connect` / `releaseStream` / `FCPublish` / `createStream` / `publish` /
  `play` / `FCUnpublish` / `deleteStream` / `closeStream`，返回 `_result` 与 `onStatus`；
- **协议控制消息**：`SetChunkSize` / `WindowAcknowledgementSize` / `SetPeerBandwidth` / `UserControl`；
- **保活**：定期 `PingRequest`，对端的 `PingRequest` 也回 `PingResponse`——拔网线的对端
  不会走 TCP 关闭流程，只有心跳能发现；
- **空闲超时 + 连接上限**：只连上不发命令的连接（扫描器常用）不会永远占资源；
- **推拉流鉴权**：`RequirePublishToken` / `RequirePlayToken` 打开后，令牌从推流地址查询串
  或 `connect` 命令对象里取，交给 `IRtmpAuthenticator` 校验，不过回 `NetStream.Failed`；
- **媒体转发**：发布者的音视频写入 `StreamHub`，订阅者读回并编码成 RTMP 分块流。

```text
OBS/ffmpeg ──RTMP(1935)──▶ RtmpServer ──▶ StreamHub ──▶ HTTP-FLV (/live/x.flv)
                                     └──▶ HLS 切片 (/live/x.m3u8 + .ts)
                                     └──▶ RTMP 拉流（另一个连接 play）
```

```csharp
builder.Services.Configure<RtmpServerOptions>(o =>
{
    o.Address = "0.0.0.0";
    o.Port = 1935;
    o.OutgoingChunkSize = 4096;
    o.PingInterval = TimeSpan.FromSeconds(30);   // 保活（0 = 关闭）
    o.IdleTimeout = TimeSpan.FromMinutes(2);     // 空闲超时
    o.MaxConnections = 2000;                     // 连接上限
    o.RequirePublishToken = true;                // 生产必须开
});

// 鉴权策略（默认 AllowAllRtmpAuthenticator）
builder.Services.AddSingleton<IRtmpAuthenticator, MyRtmpAuthenticator>();
```

`StreamHub` / `MediaStream` 是分发核心，几个关键设计：

- **GOP 缓存**：新订阅者立即补发最近一个 GOP（含序列头），起播不等下一个关键帧；
- **背压**：每个订阅者有界队列，慢观众丢最旧的帧而不是拖垮发布者；
- **生命周期**：停止发布后保留 `StreamHubOptions.UnpublishedRetention`（默认 10 秒），
  到期由 `StreamHubSweeper` 清扫——没有这一步，每条播过的流都永远占着内存；
- **统计**：Tag 数 / 字节数 / 观众数 / 丢帧数，`hub.Stats()` 一屏看完；
- 慢订阅者断开只影响自己；退订在连接关闭 / 响应结束时释放（不泄漏订阅者）。

## 媒体面：HTTP-FLV 与 HLS

```bash
# HTTP-FLV：chunked 输出，flv.js 直接播
curl -N http://127.0.0.1:8080/live/live/cam-01.flv

# HLS：实时切片 + 播放列表
curl http://127.0.0.1:8080/live/live/cam-01.m3u8
curl http://127.0.0.1:8080/live/live/cam-01/0.ts
```

- HTTP-FLV 首字节就是 FLV 头，随后是 Tag 流；GOP 补发由订阅机制保证；
- HLS 按 `HlsOptions.SegmentSeconds`（默认 2 秒）切 TS，播放列表保留最近 N 个分片；
- 两者共用同一条 `MediaStream`，一路推流同时供 FLV / HLS / RTMP 三种拉流。

## 控制面：一把令牌管推流和拉流

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

## 控制面：播放地址协商

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

## 双工通信（WebRTC 信令）

- **WHIP（推流）**：浏览器 `POST /streaming/whip/{app}/{name}` 带 SDP Offer → 201 + SDP Answer；
- **WHEP（拉流）**：浏览器 `POST /streaming/whep/{app}/{name}` 带 SDP Offer → 201 + SDP Answer；
- `DELETE` 结束会话；
- 鉴权在 SDP 交换之前：没有令牌连不上，媒体服务器不被打扰；
- SDP 交换由媒体服务器完成（`IMediaServerClient` 实现 `IWhilpExchange`；未实现返回 502 而不是假装成功）；
- 数据面双工（信令、聊天、白板、弹幕）用 `Ledon.SignalR`，媒体面用本包。

## 转流 / 转码 / 录制（外部媒体服务器）

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

- 不内置 SFU/MCU：多人实时互动（多路转发/混流）交给 SRS/ZLMediaKit，框架只做控制面与信令；
- 不实现音视频编解码器（H.264/AAC/Opus 的 encode/decode）：那是媒体服务器的职责；
- 录制文件管理（检索、播放、删除）由媒体服务器的存储与你的业务层负责。

完整文档：<https://github.com/wosledon/ledon-framework>
