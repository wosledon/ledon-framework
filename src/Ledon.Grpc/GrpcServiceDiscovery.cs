using System.Reflection;
using Grpc.Core;
using Ledon;
using Ledon.Module;

namespace Ledon.Grpc;

/// <summary>
/// gRPC 服务发现：识别 protoc 生成的「抽象基类 + 具体实现」形态。
/// 判据（与生成代码的形状对齐，不依赖约定命名）：具体类、非抽象，
/// 且其基类链上有一个标注了 <see cref="BindServiceMethodAttribute"/> 的类型
/// （protoc 生成的 <c>XxxBase</c> 正是这个形状，业务实现类继承它）。
/// </summary>
public static class GrpcServiceDiscovery
{
    /// <summary>发现被扫描程序集中的 gRPC 服务实现类（按类型全名排序，结果确定）。</summary>
    public static IReadOnlyList<Type> Discover() =>
        LedonAssemblies.Scan()
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(IsGrpcService)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>发现指定程序集中的 gRPC 服务实现类。</summary>
    public static IReadOnlyList<Type> Discover(IEnumerable<Assembly> assemblies) =>
        assemblies
            .SelectMany(assembly => assembly.GetLoadableTypes())
            .Where(IsGrpcService)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>是否为 gRPC 服务实现类（生成基类判据）。</summary>
    public static bool IsGrpcService(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
        && HasGeneratedBase(type);

    private static bool HasGeneratedBase(Type type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsDefined(typeof(BindServiceMethodAttribute), inherit: false))
                return true;
        }

        return false;
    }
}
