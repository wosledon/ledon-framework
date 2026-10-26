using Ledon.Testing.Containers;

namespace Ledon.Web.Tests;

/// <summary>Testcontainers 基建：Docker 检测不抛异常（无 Docker 环境也能安全运行）。</summary>
public class ContainerInfrastructureTests
{
    [Fact]
    public async Task Docker_availability_check_never_throws()
    {
        var exception = await Record.ExceptionAsync(() => LedonContainers.DockerAvailableAsync());

        Assert.Null(exception);
    }
}
