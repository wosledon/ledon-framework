namespace Ledon.Data;

/// <summary>
/// 标记接口：实现它的 <see cref="Microsoft.EntityFrameworkCore.DbContext"/> 会被
/// 自动注册进容器，并在启动时（先于模块初始化）自动建库/迁移。
/// 纯标记，不约束基类——直接用 DbContext 派生即可。
/// </summary>
public interface ILedonDbContext;
