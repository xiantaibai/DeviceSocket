namespace DeviceSocket.LoadTester.Configuration;

/// <summary>指定 GatherV4 下行命令的模拟终端协议应答行为。</summary>
public enum GatherV4ResponseMode
{
    /// <summary>立即返回设备编号、A3 和 PSEQ 均正确的单次确认。</summary>
    Normal,

    /// <summary>等待配置时长后返回正确确认。</summary>
    Delayed,

    /// <summary>识别下行命令但不发送任何应答。</summary>
    Drop,

    /// <summary>连续发送两次完全相同的确认，验证重复回包幂等。</summary>
    Duplicate,

    /// <summary>返回错误 A3（主站地址）的合法帧，验证服务端不会释放错误命令窗口。</summary>
    WrongA3,

    /// <summary>返回错误 PSEQ 的合法帧，验证服务端不会误关联同设备其他命令。</summary>
    WrongPseq,

    /// <summary>返回地址和序号正确的 F2 全部否认，验证服务端失败结果不能误算任务成功；追加枚举以保留既有数值兼容。</summary>
    Deny
}
