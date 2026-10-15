namespace DeviceSocket.Debugger.Core.Protocols.QGdw3761;

/// <summary>
/// 保存调试工具生成老 Gather 抄读业务回复时使用的模拟表计数据。
/// </summary>
public sealed record QGdw3761AutoReplyOptions
{
    /// <summary>
    /// 获取费率示值数量，范围 0-8；默认 4，零值表示只返回总电能示值。
    /// </summary>
    public int RateCount { get; init; } = 4;

    /// <summary>
    /// 获取模拟总电能示值，单位 kWh；默认 1000.0000，范围 0-999999.9999。
    /// </summary>
    public decimal TotalReadingKwh { get; init; } = 1000.0000m;
}
