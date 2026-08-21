using System.Text;
using System.Text.Json;

namespace DeviceSocket.Debugger.Services;

/// <summary>
/// 保存调试工具退出前需要记住的基础配置和最近一次协议参数。
/// </summary>
internal sealed class DebuggerSettings
{
    /// <summary>获取或设置最后使用的协议显示名称。</summary>
    public string ProtocolName { get; set; } = "Q/GDW 376.1";

    /// <summary>获取或设置最后使用的服务器地址。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>获取或设置最后使用的服务器端口。</summary>
    public string Port { get; set; } = "5001";

    /// <summary>获取或设置最后使用的定时心跳间隔，单位秒。</summary>
    public string HeartbeatIntervalSeconds { get; set; } = "120";

    /// <summary>按协议名和参数键保存最近修改过的指令输入值。</summary>
    public Dictionary<string, Dictionary<string, string>> InputValues { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 读写 Debugger 本地配置文件，用于跨进程退出和下次启动恢复参数。
/// </summary>
internal static class DebuggerSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeviceSocket",
        "Debugger");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// 读取本地配置文件；文件不存在、格式损坏或读取失败时返回默认配置。
    /// </summary>
    /// <returns>可用于恢复界面状态的基础配置。</returns>
    internal static DebuggerSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new DebuggerSettings();
            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<DebuggerSettings>(json, SerializerOptions) ??
                   new DebuggerSettings();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 配置文件损坏不应阻止调试工具启动，直接回退到默认值。
            return new DebuggerSettings();
        }
    }

    /// <summary>
    /// 原子写入本地配置文件，避免中途退出产生半截 JSON。
    /// </summary>
    /// <param name="settings">待保存的当前界面配置。</param>
    internal static void Save(DebuggerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(settings, SerializerOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, SettingsPath, true);
    }
}
