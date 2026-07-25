using CommunityToolkit.Mvvm.ComponentModel;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 表示根据 JSON inputs 动态生成的一项界面输入。
/// </summary>
public partial class CommandInputViewModel : ObservableObject
{
    /// <summary>
    /// 初始化动态输入并应用 JSON 默认值。
    /// </summary>
    /// <param name="definition">输入参数定义。</param>
    public CommandInputViewModel(CommandInputDefinition definition)
    {
        Definition = definition;
        value = definition.Default;
    }

    /// <summary>获取输入参数定义。</summary>
    public CommandInputDefinition Definition { get; }

    /// <summary>获取界面标签。</summary>
    public string Label => Definition.Label;

    /// <summary>获取输入说明。</summary>
    public string Description => Definition.Description;

    /// <summary>获取参数键名。</summary>
    public string Key => Definition.Key;

    /// <summary>获取下拉候选值。</summary>
    public IReadOnlyList<string> Options => Definition.Options;

    /// <summary>获取当前输入是否使用下拉列表。</summary>
    public bool IsSelect => Definition.Type.Equals("select", StringComparison.OrdinalIgnoreCase);

    /// <summary>获取当前输入是否使用文本框。</summary>
    public bool IsTextInput => !IsSelect;

    /// <summary>获取或设置用户输入值。</summary>
    [ObservableProperty]
    private string value;
}
