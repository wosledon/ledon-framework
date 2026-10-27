# Ledon.Security.Authorization

RBAC 授权：角色、权限授予、用户角色分配、通配符权限与带缓存的权限求值；接入现有 `PermissionRequirement` 策略。

```csharp
builder.Services.AddLedonSecurity(...);
builder.Services.AddLedonAuthorization(o =>
{
    o.BootstrapAdminUserIds.Add("admin");   // 首个管理员用户 ID
    o.MapEndpoints = true;                  // 默认开启
    o.ManagementPolicy = "authorization.manage";
});
```

控制器/端点直接使用权限名：

```csharp
[Authorize(Policy = "orders.read")]
[HttpGet("orders")]
public IActionResult Get() => Ok();
```

内置行为：

- 启动时创建 `admin` 角色（权限 `*`）；
- `BootstrapAdminUserIds` 中的用户自动分配到 `admin`；
- `/auth/authorization/*` 管理端点默认映射，并要求 `authorization.manage`；
- `GET /auth/authorization/me` 返回当前用户角色、权限与是否管理员；
- 角色权限支持通配符，角色分配可按租户隔离；
- 角色/分配变更自动清空权限缓存。

管理：

```csharp
var roles = scope.ServiceProvider.GetRequiredService<RoleAuthorizationService>();
await roles.CreateRoleAsync("support", ["orders.*", "users.read"]);
await roles.AssignRoleAsync(userId, "support");
```

> 直接可用仍需配置认证（`AddLedonSecurity`）并设置管理员引导用户；默认内存 `IRoleStore` 适合开发/单实例，生产请替换为数据库实现。

完整文档：<https://github.com/wosledon/ledon-framework>
