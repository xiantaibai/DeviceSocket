namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 配置 <c>commandWorkload.gatherV4</c> 下 GatherV4 <c>/Handmould/IssueCmd</c> 的业务参数；
/// 设备行政区划、终端地址和电表主键仍从当前分片的设备映射生成。
/// </summary>
public sealed class GatherV4CommandOptions
{
    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.gatherCode</c>；默认空值无效，必须与被测 Communication、DirectiveService 和 IssueWebApi 使用的采集服务编码一致。
    /// </summary>
    public string GatherCode { get; set; } = string.Empty;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.meterType</c>；默认 1 表示电表，必须填写 GatherV4 <c>MeterTypeEnum</c> 支持的正整数值。
    /// </summary>
    public int MeterType { get; set; } = 1;

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.itemCodes</c>；默认空集合无效，至少包含一个 GatherV4 已配置的采集项编码，例如实际环境使用的 <c>10_*</c> 项。
    /// </summary>
    public List<string> ItemCodes { get; set; } = [];

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.density</c>；默认 0 表示无冻结密度，其他值按 GatherV4 采集项约定解释。
    /// </summary>
    public int Density { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.point</c>；默认 0，由采集项自身决定数据点；需要冻结曲线时按业务要求设置正数。
    /// </summary>
    public int Point { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.dataTime</c>；默认 null 表示每次请求使用当前本地时间，非空时原样传入以复现固定历史时刻。
    /// </summary>
    public string? DataTime { get; set; }

    /// <summary>
    /// 获取或设置 <c>commandWorkload.gatherV4.dataTimeFormat</c>；默认 <c>yyyy-MM-dd HH:mm:ss</c>，仅在 <see cref="DataTime"/> 为空时用于格式化当前本地时间。
    /// </summary>
    public string DataTimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
}
