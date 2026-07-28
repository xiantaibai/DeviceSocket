namespace DeviceSocket.Host.Networking.Dlt645.Processing;

/// <summary>
/// DL/T 645-2007 指令路由键，由功能码、数据标识和应答方向共同确定。
/// </summary>
public readonly record struct Dlt645InstructionKey(
    byte FunctionCode,
    uint? DataIdentifier,
    bool IsResponse,
    bool IsFallback = false)
{
    /// <summary>创建精确的 645 指令键。</summary>
    public static Dlt645InstructionKey ForInstruction(
        byte functionCode,
        uint? dataIdentifier,
        bool isResponse) => new(functionCode, dataIdentifier, isResponse);

    /// <summary>未注册 645 指令的默认键。</summary>
    public static Dlt645InstructionKey Fallback { get; } = new(0, null, false, true);
}
