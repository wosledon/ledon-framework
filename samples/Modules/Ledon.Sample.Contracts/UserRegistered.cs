namespace Ledon.Sample.Contracts;

/// <summary>
/// 跨模块契约。发布方（用户模块）与消费方（通知模块）都只依赖本程序集，
/// 彼此不引用——这就是"解耦优先"的落地方式：契约独立成集，事件经总线流转。
/// </summary>
public record UserRegistered(string UserName);
