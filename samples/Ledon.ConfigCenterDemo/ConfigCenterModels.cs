using Microsoft.EntityFrameworkCore;

namespace Ledon.ConfigCenterDemo;

public enum ProjectRole
{
    Viewer = 1,
    Maintainer = 2,
    Owner = 3,
}

public static class ProjectPermissions
{
    public const string Read = "config.read";
    public const string Write = "config.write";
    public const string ManageMembers = "members.manage";
    public const string ManageProject = "project.manage";
    public const string Publish = "config.publish";

    public static IReadOnlySet<string> ForRole(ProjectRole? role) => role switch
    {
        ProjectRole.Owner => new HashSet<string>(StringComparer.Ordinal) { Read, Write, Publish, ManageMembers, ManageProject },
        ProjectRole.Maintainer => new HashSet<string>(StringComparer.Ordinal) { Read, Write, Publish },
        ProjectRole.Viewer => new HashSet<string>(StringComparer.Ordinal) { Read },
        _ => new HashSet<string>(StringComparer.Ordinal),
    };
}

public sealed class Project
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public List<ProjectEnvironment> Environments { get; set; } = [];
    public List<ProjectMember> Members { get; set; } = [];
    public List<ConfigEntry> Configs { get; set; } = [];
}

public sealed class ProjectEnvironment
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Project? Project { get; set; }
}

public sealed class ProjectMember
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ProjectRole Role { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Project? Project { get; set; }
}

public sealed class ConfigEntry
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid EnvironmentId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = "null";
    public int Version { get; set; }
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ProjectEnvironment? Environment { get; set; }
    public List<ConfigRevision> Revisions { get; set; } = [];
}

public sealed class ConfigRevision
{
    public Guid Id { get; set; }
    public Guid ConfigEntryId { get; set; }
    public int Version { get; set; }
    public string Value { get; set; } = "null";
    public string UpdatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ConfigEntry? ConfigEntry { get; set; }
}

public sealed class ConfigCenterDbContext(DbContextOptions<ConfigCenterDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectEnvironment> Environments => Set<ProjectEnvironment>();
    public DbSet<ProjectMember> Members => Set<ProjectMember>();
    public DbSet<ConfigEntry> Configs => Set<ConfigEntry>();
    public DbSet<ConfigRevision> Revisions => Set<ConfigRevision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(project => project.Id);
            entity.HasIndex(project => project.Key).IsUnique();
            entity.Property(project => project.Key).HasMaxLength(64);
            entity.Property(project => project.Name).HasMaxLength(128);
            entity.Property(project => project.Description).HasMaxLength(512);
            entity.HasMany(project => project.Environments).WithOne(env => env.Project!).HasForeignKey(env => env.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(project => project.Members).WithOne(member => member.Project!).HasForeignKey(member => member.ProjectId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(project => project.Configs).WithOne().HasForeignKey(config => config.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectEnvironment>(entity =>
        {
            entity.HasKey(env => env.Id);
            entity.HasIndex(env => new { env.ProjectId, env.Name }).IsUnique();
            entity.Property(env => env.Name).HasMaxLength(32);
        });

        modelBuilder.Entity<ProjectMember>(entity =>
        {
            entity.HasKey(member => member.Id);
            entity.HasIndex(member => new { member.ProjectId, member.UserId }).IsUnique();
            entity.Property(member => member.UserId).HasMaxLength(128);
        });

        modelBuilder.Entity<ConfigEntry>(entity =>
        {
            entity.HasKey(config => config.Id);
            entity.HasIndex(config => new { config.EnvironmentId, config.Key }).IsUnique();
            entity.Property(config => config.Key).HasMaxLength(256);
            entity.HasOne(config => config.Environment)
                .WithMany()
                .HasForeignKey(config => config.EnvironmentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(config => config.Revisions).WithOne(revision => revision.ConfigEntry!).HasForeignKey(revision => revision.ConfigEntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConfigRevision>(entity =>
        {
            entity.HasKey(revision => revision.Id);
            entity.HasIndex(revision => new { revision.ConfigEntryId, revision.Version }).IsUnique();
        });
    }
}

// ---------- API DTO ----------

public sealed record ProjectSummary(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string Role,
    bool IsSystemAdmin,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record ProjectDetail(
    Guid Id,
    string Key,
    string Name,
    string? Description,
    string Role,
    bool IsSystemAdmin,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<EnvironmentDto> Environments,
    IReadOnlyList<MemberDto> Members,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record EnvironmentDto(Guid Id, string Name, DateTimeOffset CreatedAtUtc);

public sealed record MemberDto(string UserId, string Role);

public sealed record ConfigDto(
    Guid Id,
    string Key,
    System.Text.Json.JsonElement Value,
    int Version,
    string UpdatedBy,
    DateTimeOffset UpdatedAtUtc);

public sealed record RevisionDto(int Version, System.Text.Json.JsonElement Value, string UpdatedBy, DateTimeOffset CreatedAtUtc);

public sealed record CreateProjectRequest(string Key, string Name, string? Description = null);
public sealed record UpdateProjectRequest(string Name, string? Description = null);
public sealed record CreateEnvironmentRequest(string Name);
public sealed record UpsertMemberRequest(string UserId, ProjectRole Role);
public sealed record CreateConfigRequest(System.Text.Json.JsonElement Value, bool Encrypted = false);
public sealed record AccessDto(Guid ProjectId, string UserId, string Role, bool IsSystemAdmin, IReadOnlyList<string> Permissions);
