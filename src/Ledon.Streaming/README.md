# Ledon.Streaming

**进程内独立实现**的流媒体能力：RTMP 推拉流、HTTP-FLV、HLS 切片、令牌鉴权、在线状态、事件与踢人。
不依赖 SRS / ZLMediaKit 等外部媒体服务器。

## 快速开始

```csharp
builder.Services.AddLedonStreaming(o =>
{
    o.Secret = "<从配置中心注入>";
    o.PublicRtmpUrl = "rtmp://media.example.com";
    o.PublicHttpUrl = "http://media.example.com";
});

builder.Services.Configure<RtmpServerOptions>(o =>
{
    o.Port = 1935;
    o.RequirePublishToken = true;
});
```

OBS / ffmpeg 推 `rtmp://host:1935/live/name`；播放：

- `http://host/live/name.flv`（flv.js）
- `http://host/live/name.m3u8`（Safari / 原生 HLS）

## 能力

- RTMP 服务端：握手、分块流、命令、保活、连接上限、推拉流鉴权；
- `StreamHub`：GOP 缓存、背压、过期清扫；
- HTTP-FLV / HLS 端点；
- `IStreamTokenService` + `StreamUrlBuilder`：推流/播放地址与令牌；
- `IStreamRegistry`：在线状态（谁在推、几个人在看）；
- `IStreamEventHandler`：Publish / Unpublish / Play / Stop 事件；
- `IStreamControl`：查询在线流、踢断推流/播放连接。

## 边界

- 不内置 WebRTC（无 ICE/DTLS/SRTP、无 WHIP/WHEP 端点）；
- 不做转码/混流；需要时用独立服务实现并接入事件；
- 在线状态默认进程内，多实例可替换 `IStreamRegistry`。

完整文档：<https://github.com/wosledon/ledon-framework>