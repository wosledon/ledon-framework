using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ledon.Web.Tests;

/// <summary>强类型配置测试：节名约定、DataAnnotations 与 IValidatableObject 启动校验。</summary>
public class ConfigurationTests
{
    public class SmtpOptions
    {
        [Required]
        public string Host { get; set; } = string.Empty;

        [Range(1, 65535)]
        public int Port { get; set; } = 25;
    }

    public class CrossValidatedOptions : IValidatableObject
    {
        public string UserName { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (UserName == Password)
                yield return new ValidationResult("用户名不能与密码相同");
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();

    [Fact]
    public void Section_name_defaults_to_type_name_without_options_suffix()
    {
        var services = new ServiceCollection();
        services.AddLedonOptions<SmtpOptions>(Config(("Smtp:Host", "smtp.example.com"), ("Smtp:Port", "587")));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmtpOptions>>().Value;

        Assert.Equal("smtp.example.com", options.Host);
        Assert.Equal(587, options.Port);
    }

    [Fact]
    public async Task Data_annotations_violation_fails_host_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLedonOptions<SmtpOptions>(Config(("Smtp:Port", "99999")));   // Host 缺失 + Port 越界

        using var host = builder.Build();

        // ValidateOnStart：启动即失败，而不是等首次用到才炸
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            async () => await host.StartAsync());
    }

    [Fact]
    public async Task IValidatableObject_violation_fails_host_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLedonOptions<CrossValidatedOptions>(
            Config(("CrossValidated:UserName", "same"), ("CrossValidated:Password", "same")));

        using var host = builder.Build();

        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(
            async () => await host.StartAsync());
    }
}
