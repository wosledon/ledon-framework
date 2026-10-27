using Ledon.Data;
using Ledon.Data.Seeding;
using Ledon.DataExchange;
using Ledon.Module.Abstractions;
using Ledon.MultiTenancy;
using Ledon.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var database = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "data-demo.db");
Directory.CreateDirectory(Path.GetDirectoryName(database)!);

builder.AddLedonWeb(options =>
{
    options.UseOpenApi = true;
    options.UseScalarUi = true;
});

builder.Services.AddLedonData();
builder.Services.AddDbContext<TodoDbContext>(options => options.UseSqlite($"Data Source={database}"));
builder.Services.AddScoped<ILedonDbContext>(provider => provider.GetRequiredService<TodoDbContext>());
builder.Services.AddLedonMultiTenancy();
builder.Services.AddLedonDataSeeding(options => options.RunOnStartup = false);
builder.Services.AddLedonDataExchange();

var app = builder.Build();

// 示例显式初始化：数据库结构 → 播种，便于演示每一步。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
    await db.Database.EnsureCreatedAsync();

    foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>().OrderBy(seeder => seeder.Order))
        await seeder.SeedAsync();
}

app.UseLedonMultiTenancy();   // X-Tenant-Id → ITenantContext
app.UseLedonWeb();

app.MapGet("/api/info", () => new
{
    message = "Ledon 数据示例：EF Core + SQLite + 多租户 + 播种 + CSV 导出",
    tenantHeader = "X-Tenant-Id",
    tryIt = new[]
    {
        "GET /api/todos（携带 X-Tenant-Id）",
        "GET /api/todos/all（无租户头时看全部）",
        "POST /api/todos",
        "GET /api/todos/export（CSV）",
    },
});

app.MapGet("/api/todos", async (TodoDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Todos.OrderBy(todo => todo.Id).ToListAsync(ct)));

app.MapGet("/api/todos/all", async (TodoDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Todos.IgnoreQueryFilters().OrderBy(todo => todo.Id).ToListAsync(ct)));

app.MapPost("/api/todos", async (CreateTodoRequest request, TodoDbContext db, ITenantContext tenant, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
        return Results.BadRequest(new { error = "Title 不能为空" });

    var todo = new Todo
    {
        TenantId = tenant.TenantId ?? request.TenantId ?? "tenant-a",
        Title = request.Title.Trim(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    db.Todos.Add(todo);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/todos/{todo.Id}", todo);
});

app.MapGet("/api/todos/export", async (
    TodoDbContext db,
    IDataExchange exchange,
    CancellationToken ct) =>
{
    var rows = await db.Todos
        .OrderBy(todo => todo.Id)
        .Select(todo => new TodoCsvRow
        {
            Id = todo.Id,
            TenantId = todo.TenantId,
            Title = todo.Title,
        })
        .ToListAsync(ct);

    await using var stream = new MemoryStream();
    await exchange.ExportCsvAsync(stream, rows, ct);

    return Results.File(stream.ToArray(), "text/csv", "todos.csv");
});

app.Run();

public sealed record CreateTodoRequest(string Title, string? TenantId = null);

public sealed class Todo : IMultiTenant
{
    public int Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class TodoCsvRow
{
    [DataColumn("ID", 1)] public int Id { get; set; }
    [DataColumn("租户", 2)] public string TenantId { get; set; } = string.Empty;
    [DataColumn("标题", 3)] public string Title { get; set; } = string.Empty;
}

[DisableAutoDiscovery]
public sealed class TodoDbContext(DbContextOptions<TodoDbContext> options, ITenantContext tenant)
    : DbContext(options), ILedonDbContext
{
    public string? CurrentTenantId { get; } = tenant.TenantId;

    public DbSet<Todo> Todos => Set<Todo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyTenantFilters(this, context => context.CurrentTenantId);
}

public sealed class TodoSeeder(TodoDbContext db) : IDataSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Todos.IgnoreQueryFilters().AnyAsync(cancellationToken))
            return;

        db.Todos.AddRange(
            new Todo { TenantId = "tenant-a", Title = "tenant-a 的待办", CreatedAtUtc = DateTimeOffset.UtcNow },
            new Todo { TenantId = "tenant-b", Title = "tenant-b 的待办", CreatedAtUtc = DateTimeOffset.UtcNow });

        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class DemoModule : IModule
{
    public Task OnInitializedAsync(ModuleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
