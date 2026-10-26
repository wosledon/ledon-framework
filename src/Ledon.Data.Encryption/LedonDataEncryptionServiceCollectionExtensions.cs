using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledon.Data.Encryption;

/// <summary>标记字符串属性需要透明加密（EF Core ValueConverter）。</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class EncryptedAttribute : Attribute
{
}

/// <summary>加密选项。</summary>
public sealed class LedonEncryptionOptions
{
    /// <summary>Data Protection 用途名，默认 <c>Ledon.Data.Encryption</c>。</summary>
    public string Purpose { get; set; } = "Ledon.Data.Encryption";

    /// <summary>密文前缀（用于识别已加密值），默认 <c>enc:v1:</c>。</summary>
    public string CipherPrefix { get; set; } = "enc:v1:";
}

/// <summary>字符串加解密服务。</summary>
public interface IEncryptionService
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}

/// <summary>基于 ASP.NET Core Data Protection 的加解密服务（密钥轮换由 DP 负责）。</summary>
public sealed class DataProtectionEncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;
    private readonly LedonEncryptionOptions _options;

    public DataProtectionEncryptionService(
        IDataProtectionProvider protectionProvider,
        LedonEncryptionOptions options)
    {
        _options = options;
        _protector = protectionProvider.CreateProtector(options.Purpose);
    }

    public string Protect(string plaintext) =>
        _options.CipherPrefix + _protector.Protect(plaintext);

    public string Unprotect(string ciphertext)
    {
        // 兼容未加密的历史行：没有前缀就按明文返回
        if (!ciphertext.StartsWith(_options.CipherPrefix, StringComparison.Ordinal))
            return ciphertext;

        return _protector.Unprotect(ciphertext[_options.CipherPrefix.Length..]);
    }
}

/// <summary>EF Core 值转换器：字符串落库前加密、读取时解密。</summary>
public sealed class EncryptedValueConverter : ValueConverter<string?, string?>
{
    public EncryptedValueConverter(IEncryptionService encryption)
        : base(
            value => value == null || value == "" ? value : encryption.Protect(value),
            value => value == null || value == "" ? value : encryption.Unprotect(value))
    {
    }
}

/// <summary>模型构建扩展：为标注 <see cref="EncryptedAttribute"/> 的字符串属性挂加密转换器。</summary>
public static class EncryptionModelBuilderExtensions
{
    /// <summary>
    /// 在 <c>OnModelCreating</c> 中调用：
    /// <code>
    /// protected override void OnModelCreating(ModelBuilder builder) =&gt;
    ///     builder.ApplyEncryption(encryption);
    /// </code>
    /// </summary>
    public static ModelBuilder ApplyEncryption(this ModelBuilder modelBuilder, IEncryptionService encryption)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(encryption);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.PropertyInfo?.GetCustomAttribute<EncryptedAttribute>() is null)
                    continue;

                if (property.ClrType != typeof(string))
                {
                    throw new InvalidOperationException(
                        $"{entityType.ClrType.Name}.{property.Name} 标了 [Encrypted]，但只有 string 属性支持透明加密");
                }

                property.SetValueConverter(new EncryptedValueConverter(encryption));
            }
        }

        return modelBuilder;
    }
}

/// <summary>列加密注册。</summary>
public static class LedonDataEncryptionServiceCollectionExtensions
{
    /// <summary>
    /// 注册列加密服务（在配置 DbContext 前调用）：
    /// <code>
    /// builder.Services.AddLedonDataEncryption();
    /// </code>
    /// </summary>
    public static IServiceCollection AddLedonDataEncryption(
        this IServiceCollection services,
        Action<LedonEncryptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LedonEncryptionOptions();
        configure?.Invoke(options);

        services.AddDataProtection();
        services.TryAddSingleton(options);
        services.TryAddSingleton<IEncryptionService, DataProtectionEncryptionService>();

        return services;
    }
}
