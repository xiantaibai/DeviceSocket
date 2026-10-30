namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 配置 <c>commandWorkload.gatherV4</c> 下 GatherV4 <c>/Handmould/IssueCmd</c> 的业务参数；
/// 设备行政区划、终端地址和电表主键仍从当前分片的设备映射生成。
/// </summary>
public sealed class GatherV4CommandOptions
{
    /// <summary>
    /// 配置 commandWorkload.gatherV4.validateResultData；默认 true，要求 Status 成功且 Data 中本表的 ValidData、采集项覆盖完整；
    /// false 仅兼容历史 Status 判定，报告不得据此证明任务完整完成。运行前读取，修改后需重启压测。
    /// </summary>
    public bool ValidateResultData { get; set; } = true;

    /// <summary>
    /// 配置 commandWorkload.gatherV4.expectedCommandsPerRequest；默认 1，范围 1～1024，表示预检确认的一次 HTTP 请求拆分下行命令数；
    /// 不按 ItemCodes 数量猜测，必须与服务端分组规则一致，用于一对多关联及排空分母，零值无效。
    /// </summary>
    public int ExpectedCommandsPerRequest { get; set; } = 1;

    /// <summary>
    /// 配置 commandWorkload.gatherV4.expectedResultItemCodes；默认空集合表示使用 itemCodes，非空时覆盖预期返回 DataType 集合；
    /// 用于透明转发或组合项与返回项不同的场景，每项不能为空，零元素不是关闭校验；修改后需重启压测。
    /// </summary>
    public List<string> ExpectedResultItemCodes { get; set; } = [];

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
