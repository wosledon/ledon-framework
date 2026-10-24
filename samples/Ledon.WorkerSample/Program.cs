using Ledon.Caching;
using Ledon.Data;
using Ledon.Module;
using Ledon.Queues;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// 第二消费者：非 Web 的工作进程 + 多程序集布局。
// 业务模块在独立程序集（Ledon.Sample.Users / Ledon.Sample.Notifications），
// 模块、服务、事件处理器全部靠"引用框架即纳入"的允许列表自动发现——
// Program.cs 里没有任何 AddModule / AddEventHandler / AddScoped。

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLedon();          // 模块系统（零配置，跨程序集发现）
builder.Services.AddLedonQueues();    // 后台队列（按需）
builder.Services.AddLedonCaching();   // 缓存（按需）
builder.Services.AddLedonData();      // 数据访问（按需，自动建库）

using var host = builder.Build();

await host.StartAsync();              // 模块按拓扑序自动初始化（见日志中的顺序）

Console.WriteLine();
Console.WriteLine("工作进程运行中（真实场景用 host.RunAsync() 常驻；示例 5 秒后自动停止）");
await Task.Delay(TimeSpan.FromSeconds(5));

Console.WriteLine("正在停止——触发模块逆序关闭");
await host.StopAsync();
