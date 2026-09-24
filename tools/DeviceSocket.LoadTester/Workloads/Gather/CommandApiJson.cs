using System.Text.Json;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>提供大小写不敏感的 Gather API JSON 公共字段读取，兼容 Newtonsoft 与 System.Text.Json 的命名差异。</summary>
internal static class CommandApiJson
{
    /// <summary>读取 msg 或 message 字段。</summary>
    /// <param name="root">响应 JSON 根对象。</param>
    /// <returns>服务端消息；字段不存在或不是字符串时返回 null。</returns>
    internal static string? ReadMessage(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if ((property.Name.Equals("msg", StringComparison.OrdinalIgnoreCase) ||
                 property.Name.Equals("message", StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }

    /// <summary>按大小写不敏感方式读取布尔字段。</summary>
    /// <param name="root">响应 JSON 根对象。</param>
    /// <param name="name">目标属性名称。</param>
    /// <param name="value">读取成功时返回布尔值。</param>
    /// <returns>存在匹配布尔字段时返回 true。</returns>
    internal static bool TryGetBoolean(JsonElement root, string name, out bool value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = property.Value.GetBoolean();
                return true;
            }
        }
        value = false;
        return false;
    }
}
