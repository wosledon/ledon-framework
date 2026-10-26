namespace Ledon.Data.Seeding;

/// <summary>
/// 数据播种器：在数据库结构就绪后执行（如内置管理员、字典、演示数据）。
/// 实现类会被自动发现并以 Scoped 生命周期注册——可直接注入 DbContext/仓储。
/// <para>播种应保持幂等（先查后插/按唯一键 upsert），因为每次启动都会执行。</para>
/// </summary>
public interface IDataSeeder
{
    /// <summary>执行顺序（小的先执行），默认 0。</summary>
    int Order => 0;

    /// <summary>执行播种。</summary>
    Task SeedAsync(CancellationToken cancellationToken = default);
}

/// <summary>数据播种选项。</summary>
public sealed class DataSeedingOptions
{
    /// <summary>是否启用播种，默认 <c>true</c>。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否在宿主启动时自动执行，默认 <c>true</c>。</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>播种失败是否让宿主启动失败，默认 <c>true</c>。</summary>
    public bool FailFast { get; set; } = true;
}
