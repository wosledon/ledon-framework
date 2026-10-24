using System.Text;
using System.Text.Json;

namespace Ledon.Configuration.Nacos;

/// <summary>
/// Nacos 配置内容解析：支持 properties（key=value，#注释）与 JSON（嵌套拍平为 a:b:c 键）。
/// </summary>
internal static class NacosConfigurationParser
{
    public static Dictionary<string, string?> Parse(string content)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(content))
            return result;

        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(content);
            Flatten(document.RootElement, prefix: string.Empty, result);
            return result;
        }

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string?> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}", result);
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    Flatten(item, $"{prefix}:{index++}", result);
                break;

            default:
                result[prefix] = element.ValueKind == JsonValueKind.Null ? null : element.ToString();
                break;
        }
    }
}
