# Ledon.ConfigCenterDemo

一个基于现有框架模块实现的**迷你配置中心**：

- 使用 `Ledon.Settings` 的 JSON 文件存储（原子写入）；
- 自定义 `IConfigurationSource` / `IConfigurationProvider`，把设置映射为 `IConfiguration`；
- 更新配置后触发 `ConfigurationProvider.Reload()`，`IConfiguration` 与 `IOptionsMonitor<T>` 热更新；
- 提供配置 CRUD API 与静态管理页面；
- 使用 `Ledon.Web` 自动发现/OpenAPI/Scalar。

## 运行

```bash
dotnet run --project samples/Ledon.ConfigCenterDemo
```

打开 <http://localhost:5120> 使用管理页面，或 <http://localhost:5120/scalar> 查看 API。

## 演示

```bash
# 查看当前配置
curl http://localhost:5120/api/config

# 写入并热更新
curl -X PUT http://localhost:5120/api/config/Feature:WelcomeMessage \
  -H "Content-Type: application/json" \
  -d '"Hello after reload"'

# 应用读取值（IConfiguration + IOptionsMonitor）
curl http://localhost:5120/api/effective
```

数据文件：`samples/Ledon.ConfigCenterDemo/App_Data/config-center.json`。

> 这是单实例开发示例；多实例配置中心应把 `ISettingsStore` 换成数据库/Redis，并保留相同的 `IConfigurationProvider` 热更新模式。
>
> 示例的 `/api/config` 写接口未加认证，仅用于本地演示；生产请用 `AddLedonSecurity` + `AddLedonAuthorization` 保护配置管理端点。
