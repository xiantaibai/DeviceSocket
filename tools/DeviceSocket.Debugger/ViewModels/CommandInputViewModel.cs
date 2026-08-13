using CommunityToolkit.Mvvm.ComponentModel;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 表示根据 JSON inputs 动态生成的一项界面输入。
/// </summary>
public partial class CommandInputViewModel : ObservableObject
{
    private readonly CommandInputValueMemory? _valueMemory;
    private readonly DeviceProtocol _protocol;

    /// <summary>
    /// 初始化动态输入并应用 JSON 默认值。
    /// </summary>
    /// <param name="definition">输入参数定义。</param>
    public CommandInputViewModel(CommandInputDefinition definition)
        : this(definition, default, null)
    {
    }

    /// <summary>
    /// 初始化动态输入，优先应用当前协议下用户先前修改过的值，并把后续修改写回共享记忆。
    /// </summary>
    /// <param name="definition">输入参数定义。</param>
    /// <param name="protocol">当前指令所属协议。</param>
    /// <param name="valueMemory">当前主窗口共享的用户输入记忆。</param>
    internal CommandInputViewModel(
        CommandInputDefinition definition,
        DeviceProtocol protocol,
        CommandInputValueMemory? valueMemory)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        _protocol = protocol;
        _valueMemory = valueMemory;
        // 直接给生成字段赋初值不会触发 OnValueChanged，JSON 默认值不会被误认为用户修改。
        value = valueMemory?.Resolve(protocol, definition) ?? definition.Default;
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

    /// <summary>
    /// 在界面输入发生变化时立即保存用户值，使指令树切换和重新加载都不会丢失修改。
    /// </summary>
    /// <param name="value">界面提交的最新参数值。</param>
    partial void OnValueChanged(string value)
    {
        _valueMemory?.Remember(_protocol, Key, value);
    }
}
