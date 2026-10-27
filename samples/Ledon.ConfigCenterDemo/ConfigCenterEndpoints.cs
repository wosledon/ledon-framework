using System.Text.Json;
using System.Text.RegularExpressions;
using Ledon.Security;
using Microsoft.EntityFrameworkCore;

namespace Ledon.ConfigCenterDemo;

public static partial class ConfigCenterEndpoints
{
    private const string DefaultEnvironment = "dev";

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", (ICurrentUser user) => Results.Ok(new
        {
            userId = user.UserId,
            userName = user.UserName,
            permissions = user.Permissions,
        }));

        var projects = api.MapGroup("/projects");

        projects.MapGet("/", async (
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            CancellationToken ct) =>
            Results.Ok(await authorization.ListAsync(user.UserId!, ct)));

        projects.MapPost("/", async (
            CreateProjectRequest request,
            ICurrentUser user,
            ConfigCenterDbContext db,
            ProjectAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var key = request.Key.Trim().ToLowerInvariant();

            if (!ProjectKeyPattern().IsMatch(key))
                return Results.BadRequest(new { error = "project key 必须为 2-64 位小写字母/数字/-/_" });

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "项目名称不能为空" });

            if (await db.Projects.AnyAsync(project => project.Key == key, ct))
                return Results.Conflict(new { error = $"项目 key 已存在：{key}" });

            var project = new Project
            {
                Id = Guid.NewGuid(),
                Key = key,
                Name = request.Name.Trim(),
                Description = request.Description?.Trim(),
                CreatedBy = user.UserId!,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            db.Projects.Add(project);

            foreach (var environment in new[] { "dev", "test", "prod" })
            {
                db.Environments.Add(new ProjectEnvironment
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Name = environment,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
            }

            db.Members.Add(new ProjectMember
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                UserId = user.UserId!,
                Role = ProjectRole.Owner,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/projects/{project.Id}", new ProjectSummary(
                project.Id,
                project.Key,
                project.Name,
                project.Description,
                "owner",
                false,
                project.CreatedBy,
                project.CreatedAtUtc));
        });

        projects.MapGet("/{projectId:guid}", async (
            Guid projectId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var detail = await authorization.GetDetailAsync(projectId, user.UserId!, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        projects.MapPut("/{projectId:guid}", async (
            Guid projectId,
            UpdateProjectRequest request,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageProject))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "项目名称不能为空" });

            var project = await db.Projects.FirstAsync(item => item.Id == projectId, ct);
            project.Name = request.Name.Trim();
            project.Description = request.Description?.Trim();
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { project.Id, project.Key, project.Name, project.Description });
        });

        projects.MapDelete("/{projectId:guid}", async (
            Guid projectId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageProject))
                return Results.Forbid();

            var project = await db.Projects.FirstAsync(item => item.Id == projectId, ct);
            db.Projects.Remove(project);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        projects.MapGet("/{projectId:guid}/permissions", async (
            Guid projectId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            return access is null
                ? Results.NotFound()
                : Results.Ok(new AccessDto(
                    projectId,
                    user.UserId!,
                    access.RoleName,
                    access.IsSystemAdmin,
                    access.Permissions.OrderBy(permission => permission).ToArray()));
        });

        projects.MapGet("/{projectId:guid}/environments", async (
            Guid projectId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Read))
                return Results.Forbid();

            var environments = await db.Environments.AsNoTracking()
                .Where(environment => environment.ProjectId == projectId)
                .OrderBy(environment => environment.Name)
                .Select(environment => new EnvironmentDto(environment.Id, environment.Name, environment.CreatedAtUtc))
                .ToListAsync(ct);

            return Results.Ok(environments);
        });

        projects.MapPost("/{projectId:guid}/environments", async (
            Guid projectId,
            CreateEnvironmentRequest request,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageProject))
                return Results.Forbid();

            var name = request.Name.Trim().ToLowerInvariant();

            if (!EnvironmentNamePattern().IsMatch(name))
                return Results.BadRequest(new { error = "环境名必须为 2-32 位小写字母/数字/-" });

            if (await db.Environments.AnyAsync(
                environment => environment.ProjectId == projectId && environment.Name == name,
                ct))
            {
                return Results.Conflict(new { error = $"环境已存在：{name}" });
            }

            var entity = new ProjectEnvironment
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Name = name,
                CreatedAtUtc = DateTimeOffset.UtcNow,
            };

            db.Environments.Add(entity);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/projects/{projectId}/environments/{entity.Id}", entity);
        });

        projects.MapDelete("/{projectId:guid}/environments/{environmentId:guid}", async (
            Guid projectId,
            Guid environmentId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageProject))
                return Results.Forbid();

            var environment = await db.Environments.FirstOrDefaultAsync(
                item => item.Id == environmentId && item.ProjectId == projectId,
                ct);

            if (environment is null)
                return Results.NotFound();

            if (await db.Environments.CountAsync(item => item.ProjectId == projectId, ct) <= 1)
                return Results.BadRequest(new { error = "项目至少保留一个环境" });

            db.Environments.Remove(environment);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        projects.MapGet("/{projectId:guid}/members", async (
            Guid projectId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageMembers))
                return Results.Forbid();

            var members = await db.Members.AsNoTracking()
                .Where(member => member.ProjectId == projectId)
                .OrderBy(member => member.UserId)
                .Select(member => new MemberDto(member.UserId, member.Role.ToString().ToLowerInvariant()))
                .ToListAsync(ct);

            return Results.Ok(members);
        });

        projects.MapPost("/{projectId:guid}/members", async (
            Guid projectId,
            UpsertMemberRequest request,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageMembers))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(request.UserId))
                return Results.BadRequest(new { error = "UserId 不能为空" });

            if (!Enum.IsDefined(request.Role))
                return Results.BadRequest(new { error = "Role 非法" });

            var member = await db.Members.FirstOrDefaultAsync(
                item => item.ProjectId == projectId && item.UserId == request.UserId,
                ct);

            if (member is null)
            {
                member = new ProjectMember
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    UserId = request.UserId.Trim(),
                    Role = request.Role,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                };

                db.Members.Add(member);
            }
            else
            {
                member.Role = request.Role;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new MemberDto(member.UserId, member.Role.ToString().ToLowerInvariant()));
        });

        projects.MapDelete("/{projectId:guid}/members/{userId}", async (
            Guid projectId,
            string userId,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.ManageMembers))
                return Results.Forbid();

            var member = await db.Members.FirstOrDefaultAsync(
                item => item.ProjectId == projectId && item.UserId == userId,
                ct);

            if (member is null)
                return Results.NotFound();

            if (member.Role == ProjectRole.Owner
                && await db.Members.CountAsync(
                    item => item.ProjectId == projectId && item.Role == ProjectRole.Owner,
                    ct) <= 1)
            {
                return Results.BadRequest(new { error = "不能移除最后一个 Owner" });
            }

            db.Members.Remove(member);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        projects.MapGet("/{projectId:guid}/configs", async (
            Guid projectId,
            string? environment,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Read))
                return Results.Forbid();

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound(new { error = $"环境不存在：{environment ?? DefaultEnvironment}" });

            var configs = await db.Configs.AsNoTracking()
                .Where(config => config.EnvironmentId == env.Id)
                .OrderBy(config => config.Key)
                .ToListAsync(ct);

            return Results.Ok(configs.Select(ToDto));
        });

        projects.MapGet("/{projectId:guid}/configs/export", async (
            Guid projectId,
            string? environment,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Read))
                return Results.Forbid();

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound(new { error = $"环境不存在：{environment ?? DefaultEnvironment}" });

            var configs = await db.Configs.AsNoTracking()
                .Where(config => config.EnvironmentId == env.Id)
                .OrderBy(config => config.Key)
                .ToListAsync(ct);

            return Results.Ok(configs.ToDictionary(config => config.Key, config => Parse(config.Value)));
        });

        projects.MapGet("/{projectId:guid}/configs/{key}/history", async (
            Guid projectId,
            string key,
            string? environment,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Read))
                return Results.Forbid();

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound();

            var config = await db.Configs.AsNoTracking().FirstOrDefaultAsync(
                item => item.EnvironmentId == env.Id && item.Key == key,
                ct);

            if (config is null)
                return Results.NotFound();

            var revisions = await db.Revisions.AsNoTracking()
                .Where(revision => revision.ConfigEntryId == config.Id)
                .OrderByDescending(revision => revision.Version)
                .ToListAsync(ct);

            return Results.Ok(revisions.Select(revision => new RevisionDto(
                revision.Version,
                Parse(revision.Value),
                revision.UpdatedBy,
                revision.CreatedAtUtc)));
        });

        projects.MapGet("/{projectId:guid}/configs/{key}", async (
            Guid projectId,
            string key,
            string? environment,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Read))
                return Results.Forbid();

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound();

            var config = await db.Configs.AsNoTracking().FirstOrDefaultAsync(
                item => item.EnvironmentId == env.Id && item.Key == key,
                ct);

            return config is null ? Results.NotFound() : Results.Ok(ToDto(config));
        });

        projects.MapPut("/{projectId:guid}/configs/{key}", async (
            Guid projectId,
            string key,
            string? environment,
            CreateConfigRequest request,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Write))
                return Results.Forbid();

            if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
                return Results.BadRequest(new { error = "key 不能为空且不能超过 256 字符" });

            if (request.Value.ValueKind == JsonValueKind.Undefined)
                return Results.BadRequest(new { error = "Value 不能为空" });

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound(new { error = $"环境不存在：{environment ?? DefaultEnvironment}" });

            key = key.Trim();
            var now = DateTimeOffset.UtcNow;
            var config = await db.Configs.FirstOrDefaultAsync(
                item => item.EnvironmentId == env.Id && item.Key == key,
                ct);

            if (config is null)
            {
                config = new ConfigEntry
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    EnvironmentId = env.Id,
                    Key = key,
                    Value = request.Value.GetRawText(),
                    Version = 1,
                    UpdatedBy = user.UserId!,
                    UpdatedAtUtc = now,
                };

                db.Configs.Add(config);
            }
            else
            {
                config.Value = request.Value.GetRawText();
                config.Version++;
                config.UpdatedBy = user.UserId!;
                config.UpdatedAtUtc = now;
            }

            db.Revisions.Add(new ConfigRevision
            {
                Id = Guid.NewGuid(),
                ConfigEntryId = config.Id,
                Version = config.Version,
                Value = config.Value,
                UpdatedBy = user.UserId!,
                CreatedAtUtc = now,
            });

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(config));
        });

        projects.MapDelete("/{projectId:guid}/configs/{key}", async (
            Guid projectId,
            string key,
            string? environment,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Write))
                return Results.Forbid();

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound();

            var config = await db.Configs.FirstOrDefaultAsync(
                item => item.EnvironmentId == env.Id && item.Key == key,
                ct);

            if (config is null)
                return Results.NotFound();

            db.Configs.Remove(config);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        });

        projects.MapPost("/{projectId:guid}/configs/{key}/rollback/{version:int}", async (
            Guid projectId,
            string key,
            int version,
            string? environment,
            ICurrentUser user,
            ProjectAuthorizationService authorization,
            ConfigCenterDbContext db,
            CancellationToken ct) =>
        {
            var access = await authorization.GetAccessAsync(projectId, user.UserId!, ct);

            if (access is null)
                return Results.NotFound();

            if (!access.Has(ProjectPermissions.Write))
                return Results.Forbid();

            var env = await FindEnvironmentAsync(db, projectId, environment, ct);

            if (env is null)
                return Results.NotFound();

            var config = await db.Configs.FirstOrDefaultAsync(
                item => item.EnvironmentId == env.Id && item.Key == key,
                ct);

            if (config is null)
                return Results.NotFound();

            var revision = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(
                item => item.ConfigEntryId == config.Id && item.Version == version,
                ct);

            if (revision is null)
                return Results.NotFound(new { error = $"版本不存在：{version}" });

            var now = DateTimeOffset.UtcNow;
            config.Value = revision.Value;
            config.Version++;
            config.UpdatedBy = user.UserId!;
            config.UpdatedAtUtc = now;

            db.Revisions.Add(new ConfigRevision
            {
                Id = Guid.NewGuid(),
                ConfigEntryId = config.Id,
                Version = config.Version,
                Value = config.Value,
                UpdatedBy = user.UserId!,
                CreatedAtUtc = now,
            });

            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(config));
        });
    }

    private static async Task<ProjectEnvironment?> FindEnvironmentAsync(
        ConfigCenterDbContext db,
        Guid projectId,
        string? name,
        CancellationToken ct) =>
        await db.Environments.AsNoTracking().FirstOrDefaultAsync(
            environment => environment.ProjectId == projectId
                && environment.Name == (name ?? DefaultEnvironment),
            ct);

    private static ConfigDto ToDto(ConfigEntry config) => new(
        config.Id,
        config.Key,
        Parse(config.Value),
        config.Version,
        config.UpdatedBy,
        config.UpdatedAtUtc);

    private static JsonElement Parse(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-_]{1,63}$")]
    private static partial Regex ProjectKeyPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,31}$")]
    private static partial Regex EnvironmentNamePattern();
}
