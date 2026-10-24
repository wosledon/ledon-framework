using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ledon.WebSample;

/// <summary>控制器所在程序集被自动挂为 ApplicationPart，无需手动 AddApplicationPart。</summary>
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
