# 流媒体：独立媒体面（RTMP / HTTP-FLV / HLS）

`Ledon.Streaming` 是**进程内独立实现**的流媒体能力，不依赖 SRS、ZLMediaKit 或任何外部媒体服务器：

| 层 | 内容 | 位置 |
| --- | --- | --- |
| **媒体面** | RTMP 服务端（握手 / 分块流 / 命令 / 媒体转发）、HTTP-FLV 播放、HLS 实时切片、FLV 封装、AMF0 编解码、TS 复用 | `Rtmp/` `Playback/` `Hls/` `Media/` |
| **控制面** | 推拉流令牌鉴权、在线状态、事件分发、播放地址协商、踢人/查询 | 包根目录 |

OBS / ffmpeg 推 `rtmp://host:1935/live/name`，观众用
`http://host/live/name.flv`（flv.js）或 `http://host/live/name.m3u8`（Safari / 原生 HLS）播放。
媒体数据全程在本进程内转发，不需要再部署外部媒体服务器。

## 快速搭起来

```csharp
builder.Services.AddLedonStreaming(o =>
{
    o.Secret = "<从配置中心注入>";              // 推拉流令牌签名密钥（必填）
    o.PublicRtmpUrl = "rtmp://media.example.com";
    o.PublicHttpUrl = "http://media.example.com";
});

// RTMP 监听（默认 1935），可按配置中心覆盖
builder.Services.Configure<RtmpServerOptions>(o =>
{
    o.Address = "0.0.0.0";
    o.Port = 1935;
    o.MaxConnections = 2000;
    o.RequirePublishToken = true;      // 生产必须开
});
```

`AddLedonStreaming` 会一并注册 `RtmpServer` / `HlsSegmenter` / `StreamHubSweeper`
（都是 `IHostedService`），宿主启动即监听；HTTP-FLV / HLS 端点随 `UseLedonWeb()` 自动映射。

## 媒体面：RTMP 推流 / 拉流

`RtmpServer` 是完整的 RTMP 协议栈，不是转发壳：

- **握手**：C0/C1/C2 ↔ S0/S1/S2；
- **分块流**：fmt 0/1/2/3、扩展时间戳、chunk stream 交错重组；
- **命令**：`connect` / `releaseStream` / `FCPublish` / `createStream` / `publish` /
  `play` / `FCUnpublish` / `deleteStream` / `closeStream`；
- **保活**：定期 `PingRequest`，对端 `PingResponse`；拔网线的连接靠空闲超时回收；
- **连接上限**：超过 `MaxConnections` 直接拒绝，连接结束从计数中移除；
- **推拉流鉴权**：`RequirePublishToken` / `RequirePlayToken`，令牌从推流地址查询串或 `connect`
  对象里取，交给 `IRtmpAuthenticator` 校验；
- **媒体转发**：发布者的音视频 Tag 写入 `StreamHub`，订阅者读回并编码成 RTMP 分块流。

```text
OBS/ffmpeg ──RTMP(1935)──▶ RtmpServer ──▶ StreamHub ──▶ HTTP-FLV (/live/x.flv)
                                     └──▶ HLS 切片 (/live/x.m3u8 + .ts)
                                     └──▶ RTMP 拉流（另一个连接 play）
```

`StreamHub` / `MediaStream` 的关键设计：

- **GOP 缓存**：新订阅者立即补发最近一个 GOP（含序列头），起播不等下一个关键帧；
- **背压**：每个订阅者有界队列，慢观众丢最旧的帧而不是拖垮发布者；
- **生命周期**：停止发布后保留 `StreamHubOptions.UnpublishedRetention`（默认 10 秒），
  到期由 `StreamHubSweeper` 清扫；
- **统计**：Tag 数 / 字节数 / 观众数 / 丢帧数，`hub.Stats()` 一屏看完。

## HTTP-FLV 与 HLS

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

## 控制面：令牌、在线状态、事件与踢人

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
            hls = urls.BuildHlsPlayUrl(identity, extra: userId),    // Safari / 原生 HLS
        };
    }
}
```

- **鉴权策略**：默认 `TokenStreamAuthorizer` 验签名/过期/绑定；实现 `IStreamAuthorizer` 可接自己的
  用户/租户/付费体系；没有鉴权器时由 `AllowWhenNoAuthorizer`（默认 `false`）决定；
- **在线状态**：`IStreamRegistry.Find/Snapshot` 回答谁在推、几个人在看；媒体面在推流/播放/断开时
  自动更新，活跃推流会按数据流刷新心跳，不会被离线扫描误判；
- **事件**：实现 `IStreamEventHandler` 订阅 `Publish` / `Unpublish` / `Play` / `Stop`
  （上线通知、落库、转推第三方、录制归档都挂这里）；处理器异常只记录日志，不影响媒体流；
- **控制面**：`IStreamControl.GetStreams()` 查当前流，`Kickoff(identity[, clientId])` 断开推流/播放
  连接（返回被断开的连接数）——全部由进程内 `RtmpServer` 实现。

```csharp
public class StreamOps(IStreamControl control)
{
    public int Kick(string stream) =>
        control.Kickoff(StreamIdentity.Parse($"live/{stream}"));
}
```

## 端点

| 端点 | 说明 |
| --- | --- |
| `GET /live/{app}/{name}.flv` | HTTP-FLV 播放（支持 `?token=`） |
| `GET /live/{app}/{name}.m3u8` | HLS 播放列表 |
| `GET /live/{app}/{name}/{sequence}.ts` | HLS 分片 |
| `GET /streaming/playback/{app}/{name}` | 返回在线状态、观众数与带令牌的 FLV/HLS 地址 |
| `GET /streaming/streams` | 当前在线流清单 |

## 边界

- **不做 WebRTC**：本包不内置 ICE/DTLS/SRTP，也没有 WHIP/WHEP 端点；需要亚秒级 WebRTC
  请在网关/边缘另接 WebRTC 服务，或基于 `IStreamEventHandler` 把流推给专用媒体服务。
- **不做转码/混流**：媒体面只做协议与分发；转码、混流、录制归档属于计算密集型能力，
  建议用独立进程/服务实现，再通过事件或拉流接入。
- **单实例在线状态**：`InMemoryStreamRegistry` 是进程内实现；多实例部署可替换为 Redis 实现
  （同一 `IStreamRegistry` 接口）。