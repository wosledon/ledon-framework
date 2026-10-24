using Ledon.Data;
using Ledon.Module;
using Ledon.Module.Abstractions;
using Ledon.Scheduling;
using Ledon.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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

    /// <summary>审计 + 软删除演示实体。</summary>
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={DbFile.Path}");

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplySoftDeleteFilters();   // ISoftDelete 实体的查询过滤（显式启用）
}

public class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

/// <summary>
/// 审计 + 软删除实体：实现标记接口即约定生效——CreatedAt/ModifiedAt 由拦截器自动填充，
/// 删除操作转换为 IsDeleted = true 更新（查询过滤已显式启用）。
/// </summary>
public class Note : IAudited, ISoftDelete
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public string? ModifiedBy { get; set; }
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

/// <summary>审计演示：创建返回的时间戳/操作人是框架自动填的，删除走软删除。</summary>
[ApiController]
[Route("api/notes")]
public class NotesController(TodoDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<Note>> GetAll(CancellationToken cancellationToken) =>
        await db.Notes.ToListAsync(cancellationToken);   // 已删除的行被查询过滤隐藏

    [HttpPost]
    public async Task<Note> Create(Note note, CancellationToken cancellationToken)
    {
        db.Notes.Add(note);
        await db.SaveChangesAsync(cancellationToken);
        return note;   // CreatedAt/ModifiedAt 已被拦截器填充
    }

    [HttpDelete("{id:int}")]
    public async Task<IResult> Delete(int id, CancellationToken cancellationToken)
    {
        var note = await db.Notes.FindAsync([id], cancellationToken);
        if (note is null)
            return Results.NotFound();

        db.Notes.Remove(note);   // 转换为 IsDeleted = true 的更新，行不出库
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>
/// 调度运维端点：列出任务与最近执行历史、手动触发一次（等不到 cron 时立即重跑）。
/// 生产环境应加认证授权——这里演示 API 形态。
/// </summary>
public class SchedulerAdminEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder builder)
    {
        builder.MapGet("/admin/tasks", (ISchedulerInvoker invoker, IScheduledTaskExecutionHistory history) =>
            Results.Ok(new
            {
                tasks = invoker.TaskNames,
                recent = history.Snapshot().Take(10),
            }));

        builder.MapPost("/admin/tasks/{name}/run", async (string name, ISchedulerInvoker invoker, CancellationToken ct) =>
        {
            var ran = await invoker.InvokeAsync(name, ct);
            return ran ? Results.Ok(new { triggered = name }) : Results.NotFound($"未找到定时任务 {name}");
        });
    }
}
