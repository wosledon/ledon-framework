using System.Reflection;
using Ledon;

namespace Ledon.Web.Tests;

/// <summary>Core 程序集反射辅助边界。</summary>
public class CoreEdgeTests
{
    [Fact]
    public void Null_assembly_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => AssemblyExtensions.GetLoadableTypes(null!));
    }

    [Fact]
    public void Loadable_types_are_cached_per_assembly()
    {
        var assembly = typeof(CoreEdgeTests).Assembly;

        var first = assembly.GetLoadableTypes();
        var second = assembly.GetLoadableTypes();

        Assert.Same(first, second);
        Assert.Contains(typeof(CoreEdgeTests), first);
    }
}
