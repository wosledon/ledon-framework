# Ledon.CapabilitiesDemo

无需数据库、缓存、消息队列或 Docker 的能力演示，覆盖：

- 分布式 ID：ULID / GUID v7 / Snowflake；
- TOTP（RFC 6238）与一次性恢复码；
- 统一密钥抽象（内存实现）；
- 固定窗口配额；
- RBAC 角色、通配符权限与缓存；
- CSV 导出/导入。

## 运行

```bash
dotnet run --project samples/Ledon.CapabilitiesDemo
```

所有模块均使用进程内实现，适合首次了解框架能力；生产替换为持久化/分布式实现即可。
