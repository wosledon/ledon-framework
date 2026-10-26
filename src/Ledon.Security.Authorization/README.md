# Ledon.Security.Authorization

RBAC 授权：角色、权限授予、用户角色分配、通配符权限与带缓存的权限求值；接入现有 `PermissionRequirement` 策略。

```csharp
builder.Services.AddLedonSecurity(...);
builder.Services.AddLedonAuthorization(o =>
{
    o.CacheDuration = TimeSpan.FromMinutes(1);
    o.MapEndpoints = true;          // 默认不开；开启后 /auth/authorization/*
    o.RequireAuthorization = true;
});
```

控制器/端点直接使用权限名：

```csharp
[Authorize(Policy = "orders.read")]
[HttpGet("orders")]
public IActionResult Get() => Ok();
```

管理：

```csharp
var roles = scope.ServiceProvider.GetRequiredService<RoleAuthorizationService>();
await roles.CreateRoleAsync("support", ["orders.*", "users.read"]);
await roles.AssignRoleAsync(userId, "support");
```

- 角色权限支持通配符：`orders.*` 匹配 `orders.read`/`orders.create`；
- 角色/分配变更自动清空权限缓存；
- 默认内存 `IRoleStore`，可替换为数据库/配置中心；
- 与 `Ledon.Security` 的 claim 权限并存：JWT 里的 permission 仍优先直接通过；
- 未注册 `Ledon.Security` 时也会提供按权限名动态生成策略的 PolicyProvider；
- 管理端点默认不映射，开启时默认要求授权。

完整文档：<https://github.com/wosledon/ledon-framework>
