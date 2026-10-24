namespace Ledon.Module;

/// <summary>
/// 模块系统的异常基类：依赖图错误、实例化失败、生命周期失败等都在此抛出。
/// </summary>
public class LedonModuleException : Exception
{
    public LedonModuleException(string message)
        : base(message)
    {
    }

    public LedonModuleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
