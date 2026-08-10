namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 定义设备通讯日志的稳定 Logger Category 和结构化日志类型，供提交端与 Serilog 分流共同使用。
/// </summary>
public static class DeviceTrafficLogCategories
{
    /// <summary>设备上行 Logger Category。</summary>
    public const string UpstreamCategory = "DeviceSocket.Traffic.Upstream";

    /// <summary>设备下发 Logger Category。</summary>
    public const string DownstreamCategory = "DeviceSocket.Traffic.Downstream";

    /// <summary>设备上行结构化日志类型。</summary>
    public const string UpstreamLogType = "DeviceUpstream";

    /// <summary>设备下发结构化日志类型。</summary>
    public const string DownstreamLogType = "DeviceDownstream";
}
