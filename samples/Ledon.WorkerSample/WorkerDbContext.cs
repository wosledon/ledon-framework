using Ledon.Data;
using Microsoft.EntityFrameworkCore;

namespace Ledon.WorkerSample;

/// <summary>ILedonDbContext 实现即被自动注册并在启动时自动建库。</summary>
public class WorkerDbContext : DbContext, ILedonDbContext
{
    public DbSet<WorkLog> Logs => Set<WorkLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={DbFile.Path}");
}

public class WorkLog
{
    public int Id { get; set; }
    public string Message { get; set; } = string.Empty;
}

public static class DbFile
{
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "ledon-worker.db");
}
