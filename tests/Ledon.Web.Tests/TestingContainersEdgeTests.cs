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

    [Fact]
    public async Task Docker_availability_probe_returns_false_for_failed_command()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"ledon-docker-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.Copy(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"),
            Path.Combine(directory, "docker.exe"));

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);

        try
        {
            Assert.False(await LedonContainers.DockerAvailableAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Docker_availability_probe_returns_true_for_successful_command()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"ledon-docker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.Copy(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Path.Combine(directory, "docker.exe"));

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + originalPath);

        try
        {
            Assert.True(await LedonContainers.DockerAvailableAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Directory.Delete(directory, recursive: true);
        }
    }
}
