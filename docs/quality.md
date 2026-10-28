# 模块质量评分（90+ 门禁）

> 快照：2026-10-28 · 67 个模块 · 最低 90.0 · 平均 93.5

本页定义 Ledon 模块的工程质量评分口径。目标：**每个 `src/Ledon.*` 模块 ≥ 90 分**；低于 90 分视为不达标，需补测试、文档或实现。

## 评分口径

| 维度 | 分值 | 计算方式 |
|---|---:|---|
| 测试保障 | 40 | 25 × min(1, 行覆盖率 / 80%) + 10 × min(1, 用例数 / 模块 API 密度目标) + 5 × min(1, 边界用例 / 3) |
| 文档 | 20 | 模块 README 5 + README 充实度 3 + 主 README 2 + 文档页 2 + XML 注释 8 |
| 代码质量 | 20 | 构建 0 警告 5 + 代码异味 5 + 平均文件规模 5 + 最大文件规模 5 |
| 生产就绪 | 20 | 库基线 10（DI/宿主集成 12）+ 配置 2 + 取消 2 + 日志 2 + 遥测/健康 2 |

- 覆盖率以 `dotnet test --collect:"XPlat Code Coverage"` 的合并报告为准，排除 `obj` 生成代码；80% 即满分（业界常用门槛），低于 80% 线性扣分。
- 用例密度按模块公开 API 规模计算，避免薄封装因绝对用例数少而被低估。
- 纯库不因缺少宿主/运维面被扣分；集成型包按实际具备的配置/取消/日志/遥测能力加分。
- 需要 Docker/broker 才能执行的代码（Testcontainers 夹具、Kafka/RabbitMQ 消费循环、健康检查）标注 `ExcludeFromCodeCoverage`；这些路径由设置 `LEDON_TEST_*` 或启用 Docker 的集成任务覆盖，本地单元测试默认跳过，不计入本地覆盖率。
- 边界用例按测试方法名识别（invalid/missing/duplicate/timeout/cancel/failure/...），不依赖文件名。

## 等级

- **S**：≥ 95
- **A**：90–94.9
- **不达标**：< 90

## 当前结果

| 排名 | 模块 | 总分 | 等级 | 覆盖率 | 测试/40 | 文档/20 | 代码/20 | 生产/20 |
|---:|---|---:|:---:|---:|---:|---:|---:|---:|
| 1 | `Ledon.Admin` | 98.0 | S | 92.5% | 40.0 | 20.0 | 20.0 | 18 |
| 2 | `Ledon.Data` | 98.0 | S | 85.7% | 40.0 | 20.0 | 20.0 | 18 |
| 3 | `Ledon.Module` | 98.0 | S | 82.2% | 40.0 | 20.0 | 20.0 | 18 |
| 4 | `Ledon.Queues` | 98.0 | S | 87.4% | 40.0 | 20.0 | 20.0 | 18 |
| 5 | `Ledon.Security` | 98.0 | S | 93% | 40.0 | 20.0 | 20.0 | 18 |
| 6 | `Ledon.Observability` | 97.3 | S | 92.3% | 40.0 | 20.0 | 19.3 | 18 |
| 7 | `Ledon.Queues.Kafka` | 97.3 | S | 95.5% | 40.0 | 20.0 | 19.3 | 18 |
| 8 | `Ledon.Transactions` | 96.2 | S | 77.4% | 39.2 | 19.0 | 20.0 | 18 |
| 9 | `Ledon.Application` | 96.0 | S | 83.3% | 40.0 | 20.0 | 20.0 | 16 |
| 10 | `Ledon.Data.Seeding` | 96.0 | S | 97.2% | 40.0 | 20.0 | 20.0 | 16 |
| 11 | `Ledon.Locking` | 96.0 | S | 82.2% | 40.0 | 20.0 | 20.0 | 16 |
| 12 | `Ledon.MultiTenancy` | 96.0 | S | 94.4% | 40.0 | 20.0 | 20.0 | 16 |
| 13 | `Ledon.Sip.Server` | 95.3 | S | 77.7% | 39.3 | 20.0 | 20.0 | 16 |
| 14 | `Ledon.BlobStorage` | 95.2 | S | 77.5% | 39.2 | 20.0 | 20.0 | 16 |
| 15 | `Ledon.Security.ApiKeys` | 95.2 | S | 77.3% | 39.2 | 20.0 | 20.0 | 16 |
| 16 | `Ledon.Security.Authorization` | 95.1 | S | 93.7% | 40.0 | 19.1 | 20.0 | 16 |
| 17 | `Ledon.Streaming` | 95.0 | S | 83.3% | 40.0 | 20.0 | 17.0 | 18 |
| 18 | `Ledon.Web` | 95.0 | S | 92.8% | 40.0 | 20.0 | 19.0 | 16 |
| 19 | `Ledon.Workflow` | 95.0 | S | 84.9% | 40.0 | 19.0 | 20.0 | 16 |
| 20 | `Ledon.Notifications` | 94.8 | A | 77.7% | 39.3 | 20.0 | 19.5 | 16 |
| 21 | `Ledon.Notifications.Feishu` | 94.3 | A | 74.7% | 38.3 | 20.0 | 20.0 | 16 |
| 22 | `Ledon.Search.Sync` | 94.3 | A | 84.6% | 40.0 | 18.3 | 20.0 | 16 |
| 23 | `Ledon.ApiClients` | 94.0 | A | 90.5% | 40.0 | 20.0 | 20.0 | 14 |
| 24 | `Ledon.Aspire` | 94.0 | A | 100% | 40.0 | 20.0 | 20.0 | 14 |
| 25 | `Ledon.Configuration` | 94.0 | A | 100% | 40.0 | 20.0 | 20.0 | 14 |
| 26 | `Ledon.Idempotency` | 94.0 | A | 86.5% | 40.0 | 20.0 | 20.0 | 14 |
| 27 | `Ledon.MultiTenancy.Management` | 94.0 | A | 88.1% | 40.0 | 20.0 | 20.0 | 14 |
| 28 | `Ledon.Search` | 94.0 | A | 90.8% | 40.0 | 20.0 | 20.0 | 14 |
| 29 | `Ledon.Security.ExternalLogins` | 94.0 | A | 82.7% | 40.0 | 20.0 | 20.0 | 14 |
| 30 | `Ledon.FeatureManagement` | 93.9 | A | 79.6% | 39.9 | 20.0 | 20.0 | 14 |
| 31 | `Ledon.Grpc` | 93.7 | A | 66.3% | 35.7 | 20.0 | 20.0 | 18 |
| 32 | `Ledon.Scheduling` | 93.7 | A | 76.2% | 36.5 | 20.0 | 19.3 | 18 |
| 33 | `Ledon.Settings` | 93.3 | A | 87% | 40.0 | 20.0 | 19.3 | 14 |
| 34 | `Ledon.Queues.RabbitMQ` | 93.1 | A | 71.4% | 37.3 | 20.0 | 17.8 | 18 |
| 35 | `Ledon.Configuration.Apollo` | 92.8 | A | 84.3% | 40.0 | 20.0 | 18.8 | 14 |
| 36 | `Ledon.Configuration.Nacos` | 92.8 | A | 82.5% | 40.0 | 20.0 | 18.8 | 14 |
| 37 | `Ledon.Security.TokenStores` | 92.7 | A | 91.4% | 36.7 | 20.0 | 20.0 | 16 |
| 38 | `Ledon.Caching` | 92.5 | A | 75.2% | 38.5 | 20.0 | 20.0 | 14 |
| 39 | `Ledon.Data.Migrations` | 92.5 | A | 86.1% | 40.0 | 20.0 | 18.5 | 14 |
| 40 | `Ledon.DataExchange` | 92.5 | A | 89.4% | 40.0 | 19.3 | 19.3 | 14 |
| 41 | `Ledon.Quotas` | 92.3 | A | 95.2% | 38.3 | 20.0 | 20.0 | 14 |
| 42 | `Ledon.Ids` | 92.0 | A | 87.4% | 40.0 | 20.0 | 20.0 | 12 |
| 43 | `Ledon.Security.Totp` | 92.0 | A | 90.3% | 40.0 | 20.0 | 20.0 | 12 |
| 44 | `Ledon.Serilog` | 92.0 | A | 86.4% | 40.0 | 20.0 | 20.0 | 12 |
| 45 | `Ledon.SignalR` | 92.0 | A | 92.7% | 40.0 | 20.0 | 20.0 | 12 |
| 46 | `Ledon.Sip` | 92.0 | A | 86.4% | 40.0 | 20.0 | 20.0 | 12 |
| 47 | `Ledon.Testing` | 92.0 | A | 95.1% | 40.0 | 20.0 | 20.0 | 12 |
| 48 | `Ledon.Localization` | 91.9 | A | 78% | 39.4 | 20.0 | 18.5 | 14 |
| 49 | `Ledon.Sip.Client` | 91.9 | A | 66.9% | 35.9 | 20.0 | 20.0 | 16 |
| 50 | `Ledon.Sockets.Server` | 91.9 | A | 82.4% | 37.4 | 20.0 | 18.5 | 16 |
| 51 | `Ledon.AuditLog` | 91.8 | A | 66.4% | 35.8 | 20.0 | 20.0 | 16 |
| 52 | `Ledon.OpenIddict` | 91.8 | A | 66.6% | 35.8 | 20.0 | 20.0 | 16 |
| 53 | `Ledon.Notifications.Sms` | 91.7 | A | 73.9% | 36.4 | 20.0 | 19.3 | 16 |
| 54 | `Ledon.Data.MultiTenancy` | 91.6 | A | 82% | 38.3 | 20.0 | 19.3 | 14 |
| 55 | `Ledon.Imaging` | 91.5 | A | 78.5% | 39.5 | 18.0 | 20.0 | 14 |
| 56 | `Ledon.Testing.Containers` | 91.3 | A | 94.1% | 40.0 | 20.0 | 19.3 | 12 |
| 57 | `Ledon.Webhooks` | 91.1 | A | 66.6% | 35.8 | 20.0 | 19.3 | 16 |
| 58 | `Ledon.Core` | 91.0 | A | 92.3% | 40.0 | 19.0 | 20.0 | 12 |
| 59 | `Ledon.FluentValidation` | 91.0 | A | 100% | 40.0 | 19.0 | 20.0 | 12 |
| 60 | `Ledon.Notifications.Push` | 90.9 | A | 74.5% | 36.6 | 19.0 | 19.3 | 16 |
| 61 | `Ledon.Secrets` | 90.5 | A | 68.8% | 36.5 | 20.0 | 20.0 | 14 |
| 62 | `Ledon.Sockets.Client` | 90.5 | A | 67.2% | 36.0 | 20.0 | 18.5 | 16 |
| 63 | `Ledon.ApiVersioning` | 90.3 | A | 100% | 38.3 | 20.0 | 20.0 | 12 |
| 64 | `Ledon.Data.Encryption` | 90.3 | A | 100% | 38.3 | 20.0 | 20.0 | 12 |
| 65 | `Ledon.Resilience` | 90.3 | A | 100% | 38.3 | 20.0 | 20.0 | 12 |
| 66 | `Ledon.Security.Accounts` | 90.2 | A | 67.8% | 36.2 | 20.0 | 20.0 | 14 |
| 67 | `Ledon.Protocols` | 90.0 | A | 83.6% | 40.0 | 20.0 | 20.0 | 10 |

## 复现

```bash
dotnet test Ledon.slnx -c Release \
  --collect:"XPlat Code Coverage" \
  --results-directory TestResults/quality
```

取两个测试项目的 `coverage.cobertura.xml` 合并后按上述公式计算。评分只看可复现证据，不手工调分。
