using Serilog;
using Serilog.Core;
using Serilog.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledon.Web.Tests;

/// <summary>Serilog 接入边界：无配置默认 sink、自定义 sink、空构建器。</summary>
public class SerilogEdgeTests
{
    [Fact]
    public void Null_builder_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => LedonSerilogHostBuilderExtensions.AddLedonSerilog(null!));
    }

    [Fact]
    public async Task Custom_sink_receives_logs_without_configuration()
    {
        var sink = new CollectingSink();
        var builder = WebApplication.CreateBuilder();
        builder.AddLedonSerilog(configuration => configuration
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink));

        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<SerilogEdgeTests>>();
        logger.LogInformation("serilog-edge-test");

        Assert.Contains(sink.Events, entry => entry.RenderMessage().Contains("serilog-edge-test", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_section_is_applied_without_errors()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Serilog:MinimumLevel"] = "Debug";
        builder.AddLedonSerilog();

        using var app = builder.Build();
        Assert.NotNull(app.Services.GetRequiredService<ILogger<SerilogEdgeTests>>());
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
