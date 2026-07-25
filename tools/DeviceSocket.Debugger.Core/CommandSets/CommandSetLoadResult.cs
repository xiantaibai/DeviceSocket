using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.Core.CommandSets;

/// <summary>
/// 表示指令集文件的加载结果，错误不会以异常形式泄漏到 UI。
/// </summary>
public sealed record CommandSetLoadResult(
    string FilePath,
    CommandSetDefinition? CommandSet,
    IReadOnlyList<string> Errors)
{
    /// <summary>获取指令集是否加载且校验成功。</summary>
    public bool IsSuccess => CommandSet is not null && Errors.Count == 0;
}
