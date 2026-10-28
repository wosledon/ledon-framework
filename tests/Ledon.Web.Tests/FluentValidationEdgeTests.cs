using FluentValidation;
using Ledon.Web.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Ledon.Web.Tests;

/// <summary>FluentValidation 集成边界：手动注册优先、作用域生命周期、空服务集合。</summary>
public class FluentValidationEdgeTests
{
    [Fact]
    public void Null_services_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => LedonFluentValidationServiceCollectionExtensions.AddLedonFluentValidation(null!));
    }

    [Fact]
    public void Manual_registration_after_framework_is_used()
    {
        var services = new ServiceCollection();
        services.AddLedonFluentValidation();

        var manual = new InlineValidator<ManualRequest>();
        services.AddSingleton<IValidator<ManualRequest>>(manual);

        using var provider = services.BuildServiceProvider();
        Assert.Same(manual, provider.GetRequiredService<IValidator<ManualRequest>>());
    }

    [Fact]
    public void Validators_are_scoped_to_each_scope()
    {
        var services = new ServiceCollection();
        services.AddLedonFluentValidation();

        using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var left = first.ServiceProvider.GetRequiredService<IValidator<EchoRequest>>();
        var right = second.ServiceProvider.GetRequiredService<IValidator<EchoRequest>>();

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.NotSame(left, right);
    }

    private sealed record ManualRequest(string Name);
}
