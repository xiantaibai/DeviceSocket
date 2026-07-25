using System.Collections.ObjectModel;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 表示右侧指令树中的协议、分组或具体指令节点。
/// </summary>
public sealed class CommandTreeNodeViewModel
{
    /// <summary>
    /// 初始化一个指令树节点。
    /// </summary>
    /// <param name="name">节点显示名称。</param>
    /// <param name="command">具体指令；协议和分组节点为空。</param>
    public CommandTreeNodeViewModel(string name, LoadedCommandViewModel? command = null)
    {
        Name = name;
        Command = command;
    }

    /// <summary>获取节点显示名称。</summary>
    public string Name { get; }

    /// <summary>获取子节点集合。</summary>
    public ObservableCollection<CommandTreeNodeViewModel> Children { get; } = [];

    /// <summary>获取节点关联的具体指令。</summary>
    public LoadedCommandViewModel? Command { get; }

    /// <summary>获取当前节点是否是一条可发送指令。</summary>
    public bool IsCommand => Command is not null;
}
