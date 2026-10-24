namespace Ledon.Module.Abstractions;

/// <summary>
/// 自动发现逃生门：标记的类型（及其所有嵌套类型）不参与程序集自动扫描，只能通过
/// <c>AddLedon(o =&gt; o.AddModule&lt;T&gt;())</c> 显式注册。
/// 用于测试夹具、条件装配等"不希望被默认拉入"的模块。标在容器类上即可整体屏蔽其中的模块。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DisableAutoDiscoveryAttribute : Attribute;
