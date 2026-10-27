using Microsoft.EntityFrameworkCore;

namespace Ledon.ConfigCenterDemo;

public static class ConfigCenterSeeder
{
    public static async Task SeedAsync(ConfigCenterDbContext db, ILogger logger)
    {
        if (await db.Projects.AnyAsync())
            return;

        var now = DateTimeOffset.UtcNow;

        var demo = new Project
        {
            Id = Guid.NewGuid(),
            Key = "demo",
            Name = "演示项目",
            Description = "Ledon 配置中心演示项目",
            CreatedBy = "admin",
            CreatedAtUtc = now,
        };

        var website = new Project
        {
            Id = Guid.NewGuid(),
            Key = "website",
            Name = "官网配置",
            Description = "另一个项目，用于展示项目隔离",
            CreatedBy = "admin",
            CreatedAtUtc = now,
        };

        db.Projects.AddRange(demo, website);
        db.Environments.AddRange(
            CreateEnvironment(demo.Id, "dev"), CreateEnvironment(demo.Id, "test"), CreateEnvironment(demo.Id, "prod"),
            CreateEnvironment(website.Id, "dev"), CreateEnvironment(website.Id, "prod"));

        db.Members.AddRange(
            CreateMember(demo.Id, "admin", ProjectRole.Owner),
            CreateMember(demo.Id, "user", ProjectRole.Maintainer),
            CreateMember(demo.Id, "viewer", ProjectRole.Viewer),
            CreateMember(website.Id, "admin", ProjectRole.Owner));

        var demoDev = CreateConfig(demo.Id, await EnvironmentIdAsync(db, demo.Id, "dev"), "app:name", "\"Demo Project\"");
        var demoProd = CreateConfig(demo.Id, await EnvironmentIdAsync(db, demo.Id, "prod"), "app:name", "\"Demo Project (prod)\"");
        var feature = CreateConfig(demo.Id, await EnvironmentIdAsync(db, demo.Id, "dev"), "feature:checkout", "true");

        db.Configs.AddRange(demoDev, demoProd, feature);
        db.Revisions.AddRange(
            CreateRevision(demoDev),
            CreateRevision(demoProd),
            CreateRevision(feature));

        await db.SaveChangesAsync();
        logger.LogInformation("配置中心演示数据已播种：demo / website，账号 admin、user、viewer");
    }

    private static ProjectEnvironment CreateEnvironment(Guid projectId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Name = name,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static ProjectMember CreateMember(Guid projectId, string userId, ProjectRole role) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        UserId = userId,
        Role = role,
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static ConfigEntry CreateConfig(Guid projectId, Guid environmentId, string key, string value) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        EnvironmentId = environmentId,
        Key = key,
        Value = value,
        Version = 1,
        UpdatedBy = "admin",
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };

    private static ConfigRevision CreateRevision(ConfigEntry config) => new()
    {
        Id = Guid.NewGuid(),
        ConfigEntryId = config.Id,
        Version = config.Version,
        Value = config.Value,
        UpdatedBy = config.UpdatedBy,
        CreatedAtUtc = config.UpdatedAtUtc,
    };

    private static async Task<Guid> EnvironmentIdAsync(ConfigCenterDbContext db, Guid projectId, string name)
    {
        var environment = db.Environments.Local.FirstOrDefault(
            item => item.ProjectId == projectId && item.Name == name)
            ?? await db.Environments.FirstAsync(item => item.ProjectId == projectId && item.Name == name);

        return environment.Id;
    }
}
