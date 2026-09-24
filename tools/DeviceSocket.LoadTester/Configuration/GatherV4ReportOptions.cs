namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 配置 <c>workload.gatherV4Report</c> 下由真实 GatherV4 上报样本驱动的周期业务帧；工具只改写设备地址、PSEQ、CON 和校验和，不猜测采集项数据区。
/// </summary>
public sealed class GatherV4ReportOptions
{
    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Report.frameHexTemplate</c>；默认 null，无零值语义；启用 GatherV4 周期上报时必须提供一条校验正确的终端主动上行376.1完整帧，可包含空格或逗号。
    /// </summary>
    public string? FrameHexTemplate { get; set; }

    /// <summary>
    /// 获取或设置 <c>workload.gatherV4Report.confirmationRequested</c>；默认 true，表示改写模板 SEQ 时设置 CON=1，请求 GatherV4 返回全部确认；
    /// false 用于无需 ACK 的上报项。止血版会识别该确认并避免确认循环，但尚未单独统计主动上报 ACK RTT 和超时。
    /// </summary>
    public bool ConfirmationRequested { get; set; } = true;
}
