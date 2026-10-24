using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ledon.Sample.Api;

/// <summary>
/// API 模块（独立程序集）：拥有自己的控制器、端点与 DbContext。
/// 宿主无需任何手工注册——控制器经 ApplicationPart、端点经 IEndpoint 约定自动发现。
/// </summary>
public class ApiModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.LogInformation("ApiModule 初始化（控制器/端点来自独立程序集）");
        return Task.CompletedTask;
    }
}

/// <summary>最小 API 端点：实现 IEndpoint 即被自动映射。</summary>
public class HelloEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder) =>
        builder.MapGet("/hello", () => "Hello, Ledon!");
}

/// <summary>实现 ILedonDbContext 即被自动注册并自动建库。</summary>
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

/// <summary>控制器：所在程序集自动挂为 MVC ApplicationPart。</summary>
[ApiController]
[Route("api/todos")]
public class TodosController(TodoDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<TodoItem>> GetAll(CancellationToken cancellationToken) =>
        await db.Todos.ToListAsync(cancellationToken);

    [HttpPost]
    public async Task<TodoItem> Create(TodoItem item, CancellationToken cancellationToken)
    {
        db.Todos.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }
}
