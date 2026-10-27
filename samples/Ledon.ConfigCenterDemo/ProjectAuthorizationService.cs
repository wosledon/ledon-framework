using Ledon.Security.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Ledon.ConfigCenterDemo;

public sealed record ProjectAccess(
    Project Project,
    bool IsSystemAdmin,
    ProjectRole? Role,
    IReadOnlySet<string> Permissions)
{
    public string RoleName => IsSystemAdmin ? "admin" : Role?.ToString().ToLowerInvariant() ?? "none";

    public bool Has(string permission) => IsSystemAdmin || Permissions.Contains(permission);
}

/// <summary>项目级授权：系统 admin 全通，其他用户按项目成员角色判断。</summary>
public sealed class ProjectAuthorizationService(
    ConfigCenterDbContext db,
    IPermissionEvaluator evaluator)
{
    public async Task<ProjectAccess?> GetAccessAsync(
        Guid projectId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);

        if (project is null)
            return null;

        var isAdmin = await evaluator.HasPermissionAsync(
            userId,
            "authorization.manage",
            cancellationToken: cancellationToken);

        var member = await db.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ProjectId == projectId && item.UserId == userId,
                cancellationToken);

        var role = member?.Role;
        var permissions = isAdmin
            ? ProjectPermissions.ForRole(ProjectRole.Owner)
            : ProjectPermissions.ForRole(role);

        return new ProjectAccess(project, isAdmin, role, permissions);
    }

    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var isAdmin = await evaluator.HasPermissionAsync(
            userId,
            "authorization.manage",
            cancellationToken: cancellationToken);

        var projects = isAdmin
            ? await db.Projects.AsNoTracking().OrderBy(project => project.Key).ToListAsync(cancellationToken)
            : await (
                from project in db.Projects.AsNoTracking()
                join member in db.Members.AsNoTracking() on project.Id equals member.ProjectId
                where member.UserId == userId
                orderby project.Key
                select project)
                .ToListAsync(cancellationToken);

        var roles = await db.Members.AsNoTracking()
            .Where(member => member.UserId == userId)
            .ToDictionaryAsync(member => member.ProjectId, member => member.Role, cancellationToken);

        return projects.Select(project => new ProjectSummary(
            project.Id,
            project.Key,
            project.Name,
            project.Description,
            isAdmin ? "admin" : roles.GetValueOrDefault(project.Id).ToString().ToLowerInvariant(),
            isAdmin,
            project.CreatedBy,
            project.CreatedAtUtc)).ToArray();
    }

    public async Task<ProjectDetail?> GetDetailAsync(
        Guid projectId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var access = await GetAccessAsync(projectId, userId, cancellationToken);

        if (access is null || !access.Has(ProjectPermissions.Read))
            return null;

        var environments = await db.Environments.AsNoTracking()
            .Where(environment => environment.ProjectId == projectId)
            .OrderBy(environment => environment.Name)
            .Select(environment => new EnvironmentDto(environment.Id, environment.Name, environment.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        var members = access.Has(ProjectPermissions.ManageMembers)
            ? await db.Members.AsNoTracking()
                .Where(member => member.ProjectId == projectId)
                .OrderBy(member => member.UserId)
                .Select(member => new MemberDto(member.UserId, member.Role.ToString().ToLowerInvariant()))
                .ToListAsync(cancellationToken)
            : [];

        return new ProjectDetail(
            access.Project.Id,
            access.Project.Key,
            access.Project.Name,
            access.Project.Description,
            access.RoleName,
            access.IsSystemAdmin,
            access.Permissions.OrderBy(permission => permission).ToArray(),
            environments,
            members,
            access.Project.CreatedBy,
            access.Project.CreatedAtUtc);
    }
}
