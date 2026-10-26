using Ledon.Admin;
using Ledon.Queues;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using System.Net;

namespace Ledon.Web.Tests;

/// <summary>运维端点：快照聚合、死信查询与重放。</summary>
public class AdminOpsTests
{
    private static (WebApplication App, FakeOutboxStore Store) Build()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAdmin();
        builder.Services.AddSingleton<IOutboxStore, FakeOutboxStore>();

        var app = builder.Build();
        app.UseLedonWeb();

        return (app, (FakeOutboxStore)app.Services.GetRequiredService<IOutboxStore>());
    }

    [Fact]
    public async Task Summary_reports_outbox_counts()
    {
        var (app, store) = Build();
        await app.StartAsync();

        store.Pending.Add(NewMessage());
        store.DeadLetters.Add(NewMessage());

        var client = app.GetTestClient();
        var response = await client.GetAsync("/admin/ops/summary");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"pending\":1", json);
        Assert.Contains("\"deadLetters\":1", json);

        await app.StopAsync();
    }

    [Fact]
    public async Task Dead_letters_are_listed_and_can_be_replayed()
    {
        var (app, store) = Build();
        await app.StartAsync();

        var dead = NewMessage();
        store.DeadLetters.Add(dead);

        var client = app.GetTestClient();

        var list = await client.GetAsync("/admin/ops/outbox/dead-letters");
        list.EnsureSuccessStatusCode();
        Assert.Contains(dead.Id.ToString(), await list.Content.ReadAsStringAsync());

        var replay = await client.PostAsync($"/admin/ops/outbox/dead-letters/{dead.Id}/replay", null);
        replay.EnsureSuccessStatusCode();
        Assert.Contains(dead.Id.ToString(), store.Revived.Select(id => id.ToString()));

        await app.StopAsync();
    }

    [Fact]
    public async Task Replaying_unknown_message_returns_404()
    {
        var (app, _) = Build();
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.PostAsync($"/admin/ops/outbox/dead-letters/{Guid.NewGuid()}/replay", null);

        Assert.Equal(StatusCodes.Status404NotFound, (int)response.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task Scheduler_endpoint_works_without_scheduling_services()
    {
        var (app, _) = Build();
        await app.StartAsync();

        var client = app.GetTestClient();
        var response = await client.GetAsync("/admin/ops/scheduler");

        // 未接入调度：返回 200 + 空清单（能力缺失不该让运维端点报错）
        response.EnsureSuccessStatusCode();
        Assert.Contains("\"tasks\":[]", await response.Content.ReadAsStringAsync());

        await app.StopAsync();
    }

    [Fact]
    public async Task Custom_route_prefix_is_honored()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAdmin(o => o.RoutePrefix = "ops/");
        builder.Services.AddSingleton<IOutboxStore, FakeOutboxStore>();

        var app = builder.Build();
        app.UseLedonWeb();
        await app.StartAsync();

        var client = app.GetTestClient();

        var response = await client.GetAsync("/ops/summary");
        response.EnsureSuccessStatusCode();

        // 旧默认路径不再映射
        var defaultPath = await client.GetAsync("/admin/ops/summary");
        Assert.Equal(HttpStatusCode.NotFound, defaultPath.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public void Require_authorization_without_authentication_fails_fast()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddLedonWeb();
        builder.Services.AddLedonAdmin(o => o.RequireAuthorization = true);

        var app = builder.Build();

        // 开启授权但没注册认证方案：必须启动失败，不能静默暴露运维端点
        var exception = Assert.Throws<InvalidOperationException>(() => app.UseLedonWeb());
        Assert.Contains("RequireAuthorization", exception.Message);
    }

    private static OutboxMessage NewMessage() => new()
    {
        Id = Guid.NewGuid(),
        Type = "Ledon.Tests.Ping",
        Payload = "{}",
        RetryCount = 3,
    };

    private sealed class FakeOutboxStore : IOutboxStore
    {
        public List<OutboxMessage> Pending { get; } = [];
        public List<OutboxMessage> DeadLetters { get; } = [];
        public List<Guid> Revived { get; } = [];

        public void Add(IIntegrationEvent @event)
        {
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Pending.Take(batchSize).ToArray());

        public Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
            int batchSize, string owner, TimeSpan lease, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Pending.Take(batchSize).ToArray());

        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Pending.Count);

        public Task MarkProcessedAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkFailedAsync(Guid id, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> DeleteProcessedAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(DeadLetters.Take(batchSize).ToArray());

        public Task<bool> ReviveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Revived.Add(id);
            return Task.FromResult(DeadLetters.Any(message => message.Id == id));
        }
    }
}
