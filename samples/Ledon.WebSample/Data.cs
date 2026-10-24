using Ledon.Data;
using Microsoft.EntityFrameworkCore;

namespace Ledon.WebSample;

/// <summary>实现 ILedonDbContext 即被自动注册，并在启动时自动建库。</summary>
public class TodoDbContext : DbContext, ILedonDbContext
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={DbFile.Path}");
}

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public static class DbFile
{
    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "ledon-sample.db");
}
