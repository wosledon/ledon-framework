using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace Ledon.Web.Tests;

/// <summary>Serilog 集成测试：注册、配置驱动、configure 回调。</summary>
public class SerilogTests : IDisposable
{
    private WebApplication? _app;

    private async Task<global::Serilog.ILogger> StartAsync(
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonSerilog();
        configure?.Invoke(builder);

        var app = builder.Build();
        _app = app;
        await app.StartAsync();

        return app.Services.GetRequiredService<global::Serilog.ILogger>();
    }

    [Fact]
    public async Task AddLedonSerilog_registers_serilog_logger()
    {
        // 无 Serilog 配置节 → 控制台默认 sink；global::Serilog.ILogger 进容器（ILogger 全部经 Serilog 输出）
        var serilog = await StartAsync();

        Assert.True(serilog.IsEnabled(LogEventLevel.Information));

        var logger = _app!.Services.GetRequiredService<ILogger<SerilogTests>>();
        logger.LogInformation("Serilog 冒烟（无配置节，控制台 sink）");

        await _app.StopAsync();
    }

    [Fact]
    public async Task AddLedonSerilog_reads_configuration_section()
    {
        // 配置了 Serilog 节 → 按配置装载（这里最小级别 Debug）
        var serilog = await StartAsync(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Serilog:MinimumLevel"] = "Debug" }));

        Assert.True(serilog.IsEnabled(LogEventLevel.Debug));

        await _app!.StopAsync();
    }

    [Fact]
    public async Task AddLedonSerilog_configure_callback_can_override()
    {
        var serilog = await StartAsync(builder =>
            builder.AddLedonSerilog(config => config.MinimumLevel.Verbose()));

        // configure 回调追加的配置生效（Verbose 低于默认 Information）
        Assert.True(serilog.IsEnabled(LogEventLevel.Verbose));

        await _app!.StopAsync();
    }

    public void Dispose()
    {
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        GC.SuppressFinalize(this);
    }
}
