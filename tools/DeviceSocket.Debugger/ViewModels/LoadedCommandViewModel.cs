using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 保存一条指令及其所属协议、分组和默认连接信息。
/// </summary>
public sealed record LoadedCommandViewModel(
    string CommandSetName,
    string GroupName,
    DeviceProtocol Protocol,
    int DefaultPort,
    CommandDefinition Definition);
