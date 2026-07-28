using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Host.Api.Models;

/// <summary>
/// 表示一条待下发报文的完整性和协议合法性校验结果。
/// </summary>
public sealed record FrameValidation(bool Success, IProtocolFrame? Frame, string? Code, string? Message)
{
    /// <summary>
    /// 创建包含已解析协议帧的成功结果。
    /// </summary>
    public static FrameValidation Ok(IProtocolFrame frame) => new(true, frame, null, null);

    /// <summary>
    /// 创建包含稳定错误码和可读原因的失败结果。
    /// </summary>
    public static FrameValidation Fail(string code, string message) => new(false, null, code, message);
}
