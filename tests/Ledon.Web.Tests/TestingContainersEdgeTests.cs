using Ledon.Testing.Containers;

namespace Ledon.Web.Tests;

/// <summary>容器夹具边界：无 Docker 时探针不抛异常、可取消。</summary>
public class TestingContainersEdgeTests
{
    [Fact]
    public async Task Docker_availability_probe_returns_without_throwing()
    {
        var available = await LedonContainers.DockerAvailableAsync();

        Assert.IsType<bool>(available);
    }

    [Fact]
    public async Task Docker_availability_probe_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.False(await LedonContainers.DockerAvailableAsync(cancellation.Token));
    }
}
