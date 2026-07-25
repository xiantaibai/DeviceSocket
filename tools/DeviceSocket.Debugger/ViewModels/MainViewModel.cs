using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets;
using DeviceSocket.Debugger.Core.Encoding;
using DeviceSocket.Debugger.Core.Networking;
using DeviceSocket.Debugger.Core.Protocols;
using DeviceSocket.Debugger.Services;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 维护调试工具主窗口的连接、指令选择、报文生成和收发展示状态。
/// </summary>
public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaximumDisplayCharacters = 300_000;
    private readonly CommandSetLoader _commandSetLoader = new();
    private readonly CommandEncoderRegistry _encoderRegistry = new();
    private readonly ICommandSetFilePicker _filePicker;
    private readonly DebugTcpSession _session;
    private readonly HashSet<string> _commandSetFiles = new(StringComparer.OrdinalIgnoreCase);
    private LoadedCommandViewModel? _selectedCommand;

    /// <summary>
    /// 初始化主视图模型。
    /// </summary>
    public MainViewModel()
        : this(new AvaloniaCommandSetFilePicker())
    {
    }

    /// <summary>
    /// 使用可替换的文件选择服务初始化主视图模型。
    /// </summary>
    /// <param name="filePicker">指令集文件选择服务。</param>
    public MainViewModel(ICommandSetFilePicker filePicker)
    {
        _filePicker = filePicker;
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSocket",
            "Debugger",
            "Logs");
        _session = new DebugTcpSession(logDirectory);
        _session.StateChanged += OnSessionStateChanged;
        _session.TrafficOccurred += OnTrafficOccurred;
        LogDirectory = logDirectory;
    }

    /// <summary>获取右侧指令树根节点。</summary>
    public ObservableCollection<CommandTreeNodeViewModel> CommandTree { get; } = [];

    /// <summary>获取当前指令的动态输入集合。</summary>
    public ObservableCollection<CommandInputViewModel> Inputs { get; } = [];

    /// <summary>获取日志文件保存目录。</summary>
    public string LogDirectory { get; }

    /// <summary>获取支持的协议显示名称。</summary>
    public IReadOnlyList<string> ProtocolOptions { get; } = ["Q/GDW 376.1", "DL/T 645-2007"];

    /// <summary>获取或设置服务器主机名或 IP。</summary>
    [ObservableProperty]
    private string host = "127.0.0.1";

    /// <summary>获取或设置服务器端口文本。</summary>
    [ObservableProperty]
    private string port = "5001";

    /// <summary>获取或设置当前协议显示名称。</summary>
    [ObservableProperty]
    private string selectedProtocolName = "Q/GDW 376.1";

    /// <summary>获取或设置指令树当前节点。</summary>
    [ObservableProperty]
    private CommandTreeNodeViewModel? selectedNode;

    /// <summary>获取或设置当前指令标题。</summary>
    [ObservableProperty]
    private string selectedCommandTitle = "请选择右侧指令";

    /// <summary>获取或设置当前指令说明。</summary>
    [ObservableProperty]
    private string selectedCommandDescription = "加载 JSON 指令集后，选择指令即可自动生成参数输入区域。";

    /// <summary>获取或设置已生成的原始报文。</summary>
    [ObservableProperty]
    private string generatedHex = string.Empty;

    /// <summary>获取或设置发送页展示文本。</summary>
    [ObservableProperty]
    private string sentText = string.Empty;

    /// <summary>获取或设置接收页展示文本。</summary>
    [ObservableProperty]
    private string receivedText = string.Empty;

    /// <summary>获取或设置连接状态说明。</summary>
    [ObservableProperty]
    private string connectionStatus = "未连接";

    /// <summary>获取或设置指令集加载状态。</summary>
    [ObservableProperty]
    private string commandSetStatus = "尚未加载指令集";

    /// <summary>获取或设置当前选中的收发标签，0 为发送、1 为接收。</summary>
    [ObservableProperty]
    private int selectedTabIndex;

    /// <summary>
    /// 在窗口首次打开时加载随程序发布的 JSON 指令集。
    /// </summary>
    public async Task InitializeAsync()
    {
        var builtInDirectory = Path.Combine(AppContext.BaseDirectory, "CommandSets");
        if (Directory.Exists(builtInDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(builtInDirectory, "*.json", SearchOption.AllDirectories))
                _commandSetFiles.Add(Path.GetFullPath(file));
        }

        await ReloadCommandSetsAsync();
    }

    /// <summary>
    /// 释放 TCP 调试会话。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _session.StateChanged -= OnSessionStateChanged;
        _session.TrafficOccurred -= OnTrafficOccurred;
        await _session.DisposeAsync();
    }

    partial void OnSelectedNodeChanged(CommandTreeNodeViewModel? value)
    {
        if (value?.Command is null)
            return;

        _selectedCommand = value.Command;
        SelectedCommandTitle =
            $"{value.Command.CommandSetName} / {value.Command.GroupName} / {value.Command.Definition.Name}";
        SelectedCommandDescription = value.Command.Definition.Description;
        SelectedProtocolName = ProtocolResolver.GetDisplayName(value.Command.Protocol);
        if (_session.State == DebugConnectionState.Disconnected)
            Port = value.Command.DefaultPort.ToString();

        Inputs.Clear();
        foreach (var input in value.Command.Definition.Inputs)
            Inputs.Add(new CommandInputViewModel(input));
        GeneratedHex = string.Empty;
    }

    [RelayCommand]
    private async Task OpenCommandSetAsync()
    {
        var filePath = await _filePicker.PickAsync();
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        _commandSetFiles.Add(Path.GetFullPath(filePath));
        await ReloadCommandSetsAsync();
    }

    [RelayCommand]
    private async Task ReloadCommandSetsAsync()
    {
        CommandTree.Clear();
        var loadedCount = 0;
        var allErrors = new List<string>();
        foreach (var filePath in _commandSetFiles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var result = await _commandSetLoader.LoadAsync(filePath);
            if (!result.IsSuccess)
            {
                allErrors.Add($"{Path.GetFileName(filePath)}：{string.Join("；", result.Errors)}");
                continue;
            }

            var definition = result.CommandSet!;
            var protocol = ProtocolResolver.Parse(definition.Protocol);
            var root = new CommandTreeNodeViewModel(definition.Name);
            foreach (var group in definition.Groups)
            {
                var groupNode = new CommandTreeNodeViewModel(group.Name);
                foreach (var command in group.Commands)
                {
                    var loaded = new LoadedCommandViewModel(
                        definition.Name,
                        group.Name,
                        protocol,
                        definition.DefaultPort,
                        command);
                    groupNode.Children.Add(new CommandTreeNodeViewModel(command.Name, loaded));
                }

                root.Children.Add(groupNode);
            }

            CommandTree.Add(root);
            loadedCount++;
        }

        CommandSetStatus = allErrors.Count == 0
            ? $"已加载 {loadedCount} 个指令集"
            : $"已加载 {loadedCount} 个，失败 {allErrors.Count} 个：{string.Join(Environment.NewLine, allErrors)}";

        if (CommandTree.Count > 0 && SelectedNode is null)
            SelectedNode = CommandTree[0].Children.FirstOrDefault()?.Children.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!int.TryParse(Port, out var parsedPort) || parsedPort is <= 0 or > 65535)
        {
            ConnectionStatus = "端口必须是 1-65535";
            return;
        }

        var protocol = SelectedProtocolName.StartsWith("Q/GDW", StringComparison.OrdinalIgnoreCase)
            ? DeviceProtocol.QGdw3761
            : DeviceProtocol.Dlt6452007;
        try
        {
            await _session.ConnectAsync(Host.Trim(), parsedPort, protocol);
        }
        catch (Exception exception)
        {
            ConnectionStatus = $"连接失败：{exception.Message}";
            AppendReceive(CreateLocalEntry("连接失败", exception.Message));
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _session.DisconnectAsync();
    }

    [RelayCommand]
    private void Generate()
    {
        if (!TryBuildCurrentCommand(out var result))
            return;

        GeneratedHex = HexCodec.ToHex(result!.Bytes, true);
        SelectedTabIndex = 0;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!TryBuildCurrentCommand(out var result))
            return;
        if (_session.State != DebugConnectionState.Connected)
        {
            AppendReceive(CreateLocalEntry("发送失败", "请先建立 TCP 连接。"));
            return;
        }

        GeneratedHex = HexCodec.ToHex(result!.Bytes, true);
        try
        {
            await _session.SendAsync(result.Bytes, _selectedCommand!.Definition.Name, result.Summary);
            // 指令调试的下一步通常是观察响应，发送成功后自动切换到接收页。
            SelectedTabIndex = 1;
        }
        catch (Exception exception)
        {
            AppendReceive(CreateLocalEntry("发送失败", exception.Message));
        }
    }

    [RelayCommand]
    private void ClearSend()
    {
        SentText = string.Empty;
        GeneratedHex = string.Empty;
    }

    [RelayCommand]
    private void ClearReceive()
    {
        ReceivedText = string.Empty;
    }

    private bool TryBuildCurrentCommand(out CommandBuildResult? result)
    {
        result = null;
        if (_selectedCommand is null)
        {
            AppendReceive(CreateLocalEntry("生成失败", "请先选择一条指令。"));
            return false;
        }

        try
        {
            var values = Inputs.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            result = _encoderRegistry.Build(_selectedCommand.Definition, values);
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            AppendReceive(CreateLocalEntry("生成报文失败", exception.Message));
            SelectedTabIndex = 1;
            return false;
        }
    }

    private void OnSessionStateChanged(object? sender, DebugConnectionStateChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() => ConnectionStatus = eventArgs.Message);
    }

    private void OnTrafficOccurred(object? sender, DebugTrafficEntry entry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (entry.Direction == TrafficDirection.Send)
                SentText = AppendBounded(SentText, FormatEntry(entry));
            else
                AppendReceive(entry);
        });
    }

    private void AppendReceive(DebugTrafficEntry entry)
    {
        ReceivedText = AppendBounded(ReceivedText, FormatEntry(entry));
    }

    private static string FormatEntry(DebugTrafficEntry entry)
    {
        var direction = entry.Direction switch
        {
            TrafficDirection.Send => "发送",
            TrafficDirection.Receive => "接收",
            TrafficDirection.Error => "错误",
            _ => "状态"
        };
        var builder = new StringBuilder()
            .Append('[').Append(entry.Timestamp.ToString("HH:mm:ss.fff")).Append("] ")
            .Append(direction).Append(" | ").AppendLine(entry.Title);
        if (!string.IsNullOrWhiteSpace(entry.RawHex))
            builder.Append("HEX: ").AppendLine(entry.RawHex);
        if (!string.IsNullOrWhiteSpace(entry.Details))
            builder.AppendLine(entry.Details);
        return builder.AppendLine(new string('-', 72)).ToString();
    }

    private static DebugTrafficEntry CreateLocalEntry(string title, string details) =>
        new(DateTimeOffset.Now, TrafficDirection.Error, title, string.Empty, details);

    private static string AppendBounded(string original, string addition)
    {
        var combined = original + addition;
        return combined.Length <= MaximumDisplayCharacters
            ? combined
            : combined[^MaximumDisplayCharacters..];
    }
}
