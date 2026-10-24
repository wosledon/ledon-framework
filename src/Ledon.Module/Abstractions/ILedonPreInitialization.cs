namespace Ledon.Module.Abstractions;

/// <summary>
/// 模块初始化前的前置任务：在 <see cref="IModuleManager.InitializeAsync"/> 开始初始化
/// 任何模块之前执行。用于"必须先于模块就绪"的基础设施（如数据库建库/迁移）。
/// 执行顺序按容器注册顺序；任一任务失败将中止模块初始化（此时还没有模块启动，无需回滚）。
/// </summary>
public interface ILedonPreInitialization
{
    Task ExecuteAsync(CancellationToken cancellationToken = default);
}
