# Ledon.AuditLog

操作审计流水：谁在何时对哪个实体做了什么、改了哪些属性（before/after）。合规与排障刚需。

```csharp
builder.Services.AddLedonAudit<AppDbContext>();   // 持久化（AppDbContext 需含 DbSet<AuditEntry>）
app.UseLedonAuditContext();                      // 自动填操作人 / IP / 请求 ID
```

只依赖 EF 的 SaveChanges 拦截器，业务代码零改动：

- 新增/修改/删除自动记录；修改只记**真实变化**的属性；
- 审计写在业务事务提交**之后**：业务回滚不会留下"改过了"的假记录；
- 审计自身失败只记日志，不回滚业务（审计不能拖垮业务）；
- 操作人来自登录声明（`sub`/NameIdentifier），后台任务用 `AuditContext.BeginScope(userId, name)` 声明；
- 查询：`IAuditStore.QueryAsync(entityType, entityId)`；保留策略：`PurgeOlderThanAsync`（默认 90 天）；
- 内存实现（`AddLedonAudit()`）供开发/单实例；`AddLedonAudit<TContext>()` 持久化。

完整文档：<https://github.com/wosledon/ledon-framework>
