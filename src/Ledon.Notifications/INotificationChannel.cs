namespace Ledon.Notifications;

/// <summary>
/// 渠道声明：发送器标明自己处理哪个渠道（路由依据）。
/// 新渠道（飞书/短信/推送）实现本接口并注册进容器，路由自动纳入——不需要改路由代码。
/// </summary>
public interface INotificationChannel : INotificationSender
{
    /// <summary>渠道名（小写，如 <c>email</c> / <c>feishu</c> / <c>sms</c>）。</summary>
    string Channel { get; }
}
