using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets;
using DeviceSocket.Debugger.Core.CommandSets.Models;
using DeviceSocket.Debugger.Core.Encoding;
using DeviceSocket.Debugger.Core.Networking;
using DeviceSocket.Debugger.Core.Protocols;
using DeviceSocket.Debugger.Core.Protocols.QGdw3761;
using DeviceSocket.Debugger.Services;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 维护调试工具主窗口的连接、指令选择、报文生成和收发展示状态。
/// </summary>
public partial class MainViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaximumTextSnapshotCharacters = 300_000;
    private const int MaximumReceivedDisplayCharacters = 5_000_000;
    private const int MaximumReceivedEntryCount = 10_000;
    private const int MaximumHeartbeatEntryCount = 1_000;
    private const int MaximumPendingTrafficCount = 20_000;
    private const int TrafficFlushBatchSize = 250;
    private const string AllInstructionFilter = "全部指令";
    private const string HeartbeatCommandId = "qgdw3761.heartbeat";
    private const string GatherReadingResponseCommandId = "qgdw3761.gather-reading-response";
    private readonly CommandSetLoader _commandSetLoader = new();
    private readonly CommandInputValueMemory _commandInputValueMemory = new();
    private readonly CommandEncoderRegistry _encoderRegistry = new();
    private readonly ICommandSetFilePicker _filePicker;
    private readonly DebugTcpSession _session;
    private readonly DebugTrafficCorrelator _trafficCorrelator = new();
    private readonly object _pendingTrafficSync = new();
    private readonly Queue<DebugTrafficEntry> _pendingTrafficEntries = new();
    private readonly List<ReceivedTrafficItemViewModel> _receivedHistory = [];
    private readonly HashSet<string> _instructionFilterValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _trafficFlushTimer;
    private readonly HashSet<string> _commandSetFiles = new(StringComparer.OrdinalIgnoreCase);
    private LoadedCommandViewModel? _selectedCommand;
    private CancellationTokenSource? _heartbeatCancellation;
    private Task? _heartbeatLoopTask;
    private int _receivedDisplayCharacters;
    private int _businessMessageCount;
    private int _heartbeatMessageCount;
    private int _statusMessageCount;

    /// <summary>
    /// 保存启动定时心跳时的指令定义和参数快照，使后续切换指令或修改界面参数不会影响已启动的周期发送。
    /// </summary>
    /// <param name="Definition">生成心跳报文使用的指令定义。</param>
    /// <param name="Values">启动时冻结的编码参数。</param>
    /// <param name="CommandName">用于发送日志展示的指令名称。</param>
    private sealed record AutoHeartbeatContext(
        CommandDefinition Definition,
        IReadOnlyDictionary<string, string> Values,
        string CommandName);

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
        _trafficFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _trafficFlushTimer.Tick += OnTrafficFlushTimerTick;
        _trafficFlushTimer.Start();
        LogDirectory = logDirectory;
    }

    /// <summary>获取右侧指令树根节点。</summary>
    public ObservableCollection<CommandTreeNodeViewModel> CommandTree { get; } = [];

    /// <summary>获取当前指令的动态输入集合。</summary>
    public ObservableCollection<CommandInputViewModel> Inputs { get; } = [];

    /// <summary>获取接收页按单条报文组织的通信记录。</summary>
    public ObservableCollection<ReceivedTrafficItemViewModel> ReceivedEntries { get; } = [];

    /// <summary>获取接收中心可选择的 AFN/FN 或功能码筛选项。</summary>
    public ObservableCollection<string> InstructionFilterOptions { get; } = [AllInstructionFilter];

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

    /// <summary>获取或设置接收记录的兼容文本快照；界面使用 <see cref="ReceivedEntries"/> 分条展示。</summary>
    [ObservableProperty]
    private string receivedText = string.Empty;

    /// <summary>获取或设置连接状态说明。</summary>
    [ObservableProperty]
    private string connectionStatus = "未连接";

    /// <summary>获取或设置接收中心当前选中的记录。</summary>
    [ObservableProperty]
    private ReceivedTrafficItemViewModel? selectedReceivedEntry;

    /// <summary>获取或设置接收中心当前分类筛选；默认只看业务回复。</summary>
    [ObservableProperty]
    private ReceiveTrafficFilter selectedReceiveFilter = ReceiveTrafficFilter.Business;

    /// <summary>获取或设置设备号、标题、HEX 和详情的搜索文本；空白表示不过滤。</summary>
    [ObservableProperty]
    private string receiveSearchText = string.Empty;

    /// <summary>获取或设置当前 AFN/FN 或功能码筛选项；“全部指令”表示不过滤。</summary>
    [ObservableProperty]
    private string selectedInstructionFilter = AllInstructionFilter;

    /// <summary>获取或设置是否暂停把后台通信记录刷新到界面；TCP 接收和文件日志不受影响。</summary>
    [ObservableProperty]
    private bool isReceivePaused;

    /// <summary>获取或设置新记录到达时是否自动滚动到列表底部；默认开启。</summary>
    [ObservableProperty]
    private bool autoScrollReceived = true;

    /// <summary>获取或设置暂停或批量刷新期间等待进入界面的通信记录数。</summary>
    [ObservableProperty]
    private int pendingReceiveCount;

    /// <summary>获取或设置接收中心当前显示数量、缓存数量和等待数量说明。</summary>
    [ObservableProperty]
    private string receiveViewStatus = "显示 0 / 缓存 0";

    /// <summary>获取或设置业务分类按钮的数量标签。</summary>
    [ObservableProperty]
    private string businessFilterLabel = "业务回复 0";

    /// <summary>获取或设置心跳分类按钮的数量标签。</summary>
    [ObservableProperty]
    private string heartbeatFilterLabel = "心跳 0";

    /// <summary>获取或设置异常和状态分类按钮的数量标签。</summary>
    [ObservableProperty]
    private string statusFilterLabel = "异常/状态 0";

    /// <summary>获取或设置全部记录分类按钮的数量标签。</summary>
    [ObservableProperty]
    private string allFilterLabel = "全部 0";

    /// <summary>获取业务回复分类是否处于选中状态。</summary>
    public bool IsBusinessReceiveFilterSelected => SelectedReceiveFilter == ReceiveTrafficFilter.Business;

    /// <summary>获取心跳分类是否处于选中状态。</summary>
    public bool IsHeartbeatReceiveFilterSelected => SelectedReceiveFilter == ReceiveTrafficFilter.Heartbeat;

    /// <summary>获取异常和状态分类是否处于选中状态。</summary>
    public bool IsStatusReceiveFilterSelected => SelectedReceiveFilter == ReceiveTrafficFilter.Status;

    /// <summary>获取全部记录分类是否处于选中状态。</summary>
    public bool IsAllReceiveFilterSelected => SelectedReceiveFilter == ReceiveTrafficFilter.All;

    /// <summary>获取详情面板当前是否存在选中记录。</summary>
    public bool HasSelectedReceivedEntry => SelectedReceivedEntry is not null;

    /// <summary>获取详情面板当前是否应显示未选择提示。</summary>
    public bool IsReceiveDetailEmpty => !HasSelectedReceivedEntry;

    /// <summary>获取暂停或继续刷新按钮的动态文字。</summary>
    public string PauseReceiveButtonText => IsReceivePaused
        ? $"继续刷新 ({PendingReceiveCount})"
        : "暂停刷新";

    /// <summary>获取或设置 TCP 调试会话当前连接状态。</summary>
    [ObservableProperty]
    private DebugConnectionState connectionState = DebugConnectionState.Disconnected;

    /// <summary>获取连接按钮是否应显示；连接完成后由断开按钮替代。</summary>
    public bool IsConnectActionVisible =>
        ConnectionState is DebugConnectionState.Disconnected or DebugConnectionState.Connecting;

    /// <summary>获取断开按钮是否应显示；已连接和断开过程中保持显示。</summary>
    public bool IsDisconnectActionVisible => !IsConnectActionVisible;

    /// <summary>获取当前是否允许发起新连接。</summary>
    public bool CanConnect => ConnectionState == DebugConnectionState.Disconnected;

    /// <summary>获取当前是否允许手动断开连接。</summary>
    public bool CanDisconnect => ConnectionState == DebugConnectionState.Connected;

    /// <summary>获取或设置指令集加载状态。</summary>
    [ObservableProperty]
    private string commandSetStatus = "尚未加载指令集";

    /// <summary>获取或设置当前选中的收发标签，0 为发送、1 为接收。</summary>
    [ObservableProperty]
    private int selectedTabIndex;

    /// <summary>获取或设置当前指令是否为 376.1 心跳指令，用于显示定时发送配置。</summary>
    [ObservableProperty]
    private bool isHeartbeatCommand;

    /// <summary>获取或设置当前指令是否为 AFN=0CH/F129 抄读回复指令，用于显示下发报文解析区。</summary>
    [ObservableProperty]
    private bool isGatherReadingResponseCommand;

    /// <summary>获取或设置定时心跳发送间隔，单位秒；默认 120 秒。</summary>
    [ObservableProperty]
    private string heartbeatIntervalSeconds = "120";

    /// <summary>获取或设置定时心跳运行状态说明。</summary>
    [ObservableProperty]
    private string autoHeartbeatStatus = "未启动";

    /// <summary>获取或设置当前是否正在定时发送心跳。</summary>
    [ObservableProperty]
    private bool isAutoHeartbeatRunning;

    /// <summary>获取当前是否允许启动定时心跳；运行中为 false，用于置灰启动按钮。</summary>
    public bool CanStartAutoHeartbeat => !IsAutoHeartbeatRunning;

    /// <summary>获取当前指令是否存在右侧扩展配置卡片。</summary>
    public bool HasSpecialConfiguration => IsHeartbeatCommand || IsGatherReadingResponseCommand;

    /// <summary>获取或设置用于回填抄读回复参数的下发报文 HEX。</summary>
    [ObservableProperty]
    private string downstreamHex = string.Empty;

    /// <summary>获取或设置下发报文解析回填操作的状态说明。</summary>
    [ObservableProperty]
    private string downstreamApplyStatus = string.Empty;

    /// <summary>
    /// 在窗口首次打开时加载随程序发布的 JSON 指令集。
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = DebuggerSettingsStore.Load();
        HeartbeatIntervalSeconds = settings.HeartbeatIntervalSeconds;
        RestoreCommandInputMemory(settings.InputValues);

        var builtInDirectory = Path.Combine(AppContext.BaseDirectory, "CommandSets");
        if (Directory.Exists(builtInDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(builtInDirectory, "*.json", SearchOption.AllDirectories))
                _commandSetFiles.Add(Path.GetFullPath(file));
        }

        await ReloadCommandSetsAsync();
        ApplySavedProtocol(settings.ProtocolName);
        Host = settings.Host;
        Port = settings.Port;
    }

    /// <summary>
    /// 释放 TCP 调试会话。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _trafficFlushTimer.Stop();
        _trafficFlushTimer.Tick -= OnTrafficFlushTimerTick;
        SaveSettings();
        await StopAutoHeartbeatAsync();
        _session.StateChanged -= OnSessionStateChanged;
        _session.TrafficOccurred -= OnTrafficOccurred;
        await _session.DisposeAsync();
    }

    /// <summary>
    /// 切换具体指令时重建动态输入区，并优先恢复当前协议下用户已经修改过的同名参数。
    /// </summary>
    /// <param name="value">指令树新选中的节点；协议或分组节点不重建输入。</param>
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

        IsHeartbeatCommand = string.Equals(
            value.Command.Definition.Id,
            HeartbeatCommandId,
            StringComparison.OrdinalIgnoreCase);
        IsGatherReadingResponseCommand = string.Equals(
            value.Command.Definition.Id,
            GatherReadingResponseCommandId,
            StringComparison.OrdinalIgnoreCase);
        DownstreamHex = string.Empty;
        DownstreamApplyStatus = string.Empty;

        Inputs.Clear();
        foreach (var input in value.Command.Definition.Inputs)
        {
            Inputs.Add(new CommandInputViewModel(
                input,
                value.Command.Protocol,
                _commandInputValueMemory));
        }
        GeneratedHex = string.Empty;
    }

    /// <summary>
    /// 在自动心跳运行状态变化时同步启动按钮的可用状态。
    /// </summary>
    /// <param name="value">新的运行状态。</param>
    partial void OnIsAutoHeartbeatRunningChanged(bool value) =>
        OnPropertyChanged(nameof(CanStartAutoHeartbeat));

    /// <summary>
    /// 在心跳指令可见性变化时刷新右侧扩展配置卡片的自适应布局状态。
    /// </summary>
    /// <param name="value">当前指令是否为心跳指令。</param>
    partial void OnIsHeartbeatCommandChanged(bool value) =>
        OnPropertyChanged(nameof(HasSpecialConfiguration));

    /// <summary>
    /// 在抄读回复指令可见性变化时刷新右侧扩展配置卡片的自适应布局状态。
    /// </summary>
    /// <param name="value">当前指令是否为抄读回复指令。</param>
    partial void OnIsGatherReadingResponseCommandChanged(bool value) =>
        OnPropertyChanged(nameof(HasSpecialConfiguration));

    /// <summary>
    /// 在接收分类变化时刷新按钮选中样式和可见记录。
    /// </summary>
    /// <param name="value">新的接收分类筛选。</param>
    partial void OnSelectedReceiveFilterChanged(ReceiveTrafficFilter value)
    {
        OnPropertyChanged(nameof(IsBusinessReceiveFilterSelected));
        OnPropertyChanged(nameof(IsHeartbeatReceiveFilterSelected));
        OnPropertyChanged(nameof(IsStatusReceiveFilterSelected));
        OnPropertyChanged(nameof(IsAllReceiveFilterSelected));
        RefreshReceivedEntries();
    }

    /// <summary>
    /// 在搜索文字变化时即时刷新可见记录。
    /// </summary>
    /// <param name="value">新的设备号或关键字搜索文本。</param>
    partial void OnReceiveSearchTextChanged(string value) => RefreshReceivedEntries();

    /// <summary>
    /// 在 AFN/FN 或功能码筛选变化时刷新可见记录。
    /// </summary>
    /// <param name="value">新的指令筛选项。</param>
    partial void OnSelectedInstructionFilterChanged(string value) => RefreshReceivedEntries();

    /// <summary>
    /// 在暂停状态变化时更新按钮和接收中心状态文字。
    /// </summary>
    /// <param name="value">是否暂停界面刷新。</param>
    partial void OnIsReceivePausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseReceiveButtonText));
        UpdateReceiveViewStatus();
    }

    /// <summary>
    /// 在等待刷新数量变化时更新暂停按钮和状态文字。
    /// </summary>
    /// <param name="value">当前等待进入界面的记录数。</param>
    partial void OnPendingReceiveCountChanged(int value)
    {
        OnPropertyChanged(nameof(PauseReceiveButtonText));
        UpdateReceiveViewStatus();
    }

    /// <summary>
    /// 在详情选择变化时切换详情正文和空状态提示。
    /// </summary>
    /// <param name="value">新的接收记录；未选择时为空。</param>
    partial void OnSelectedReceivedEntryChanged(ReceivedTrafficItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelectedReceivedEntry));
        OnPropertyChanged(nameof(IsReceiveDetailEmpty));
    }

    /// <summary>
    /// 在 TCP 状态变化时同步连接和断开按钮的显示及可用状态。
    /// </summary>
    /// <param name="value">新的 TCP 连接状态。</param>
    partial void OnConnectionStateChanged(DebugConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnectActionVisible));
        OnPropertyChanged(nameof(IsDisconnectActionVisible));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    /// <summary>
    /// 在用户切换到非 Q/GDW 376.1 协议时停止该协议下运行的定时心跳。
    /// </summary>
    /// <param name="value">新的协议显示名称。</param>
    partial void OnSelectedProtocolNameChanged(string value)
    {
        if (_heartbeatCancellation is not null &&
            !value.StartsWith("Q/GDW", StringComparison.OrdinalIgnoreCase))
        {
            _ = StopAutoHeartbeatAsync();
        }
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

    /// <summary>
    /// 使用当前服务器和协议参数建立 TCP 调试连接。
    /// </summary>
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
            _trafficCorrelator.Clear();
            await _session.ConnectAsync(Host.Trim(), parsedPort, protocol);
        }
        catch (Exception exception)
        {
            ConnectionStatus = $"连接失败：{exception.Message}";
            AppendReceive(CreateLocalEntry("连接失败", exception.Message));
        }
    }

    /// <summary>
    /// 停止定时心跳并主动断开当前 TCP 调试连接。
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_heartbeatCancellation is not null)
            await StopAutoHeartbeatAsync();
        await _session.DisconnectAsync();
    }

    /// <summary>
    /// 按当前心跳参数启动定时发送；启动后每个周期生成并发送一条心跳报文。
    /// </summary>
    [RelayCommand]
    private async Task StartAutoHeartbeatAsync()
    {
        if (_heartbeatCancellation is not null)
        {
            AutoHeartbeatStatus = "定时心跳已经在运行。";
            return;
        }
        if (!IsHeartbeatCommand || _selectedCommand?.Definition.Id != HeartbeatCommandId)
        {
            AutoHeartbeatStatus = "请先选择 376.1 心跳指令。";
            return;
        }
        if (!int.TryParse(HeartbeatIntervalSeconds.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var intervalSeconds) ||
            intervalSeconds <= 0)
        {
            AutoHeartbeatStatus = "定时心跳间隔必须是大于 0 的整数秒。";
            return;
        }
        if (_session.State != DebugConnectionState.Connected)
        {
            AutoHeartbeatStatus = "请先建立 TCP 连接。";
            return;
        }

        var values = Inputs.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.OrdinalIgnoreCase);
        values["heartbeatTime"] = string.Empty;
        var context = new AutoHeartbeatContext(
            _selectedCommand!.Definition,
            values,
            _selectedCommand.Definition.Name);
        var cancellation = new CancellationTokenSource();
        _heartbeatCancellation = cancellation;
        IsAutoHeartbeatRunning = true;
        AutoHeartbeatStatus = $"已启动，每 {intervalSeconds} 秒发送一次心跳。";
        _heartbeatLoopTask = RunAutoHeartbeatLoopAsync(intervalSeconds, context, cancellation.Token);
    }

    /// <summary>
    /// 停止当前正在运行的定时心跳循环。
    /// </summary>
    [RelayCommand]
    private async Task StopAutoHeartbeatAsync()
    {
        if (_heartbeatCancellation is null)
        {
            AutoHeartbeatStatus = "未启动";
            return;
        }

        _heartbeatCancellation.Cancel();
        var loopTask = _heartbeatLoopTask;
        if (loopTask is not null)
        {
            try
            {
                await loopTask;
            }
            catch (OperationCanceledException)
            {
                // 主动停止由取消令牌触发，属于正常结束。
            }
        }

        ResetAutoHeartbeatState();
        AutoHeartbeatStatus = "已停止";
    }

    /// <summary>
    /// 解析用户粘贴的下发读表报文，并把地址、MSA、群地址、RSEQ 和 PN 回填到抄读回复输入区。
    /// </summary>
    [RelayCommand]
    private void ApplyDownstream()
    {
        try
        {
            var parameters = QGdw3761ReadingResponseParameterResolver.Resolve(DownstreamHex);
            SetInputValue("administrativeDivision", parameters.AdministrativeDivision);
            SetInputValue("terminalAddress", parameters.TerminalAddress.ToString(CultureInfo.InvariantCulture));
            SetInputValue("masterStationAddress", parameters.MasterStationAddress.ToString(CultureInfo.InvariantCulture));
            SetInputValue("groupAddress", parameters.GroupAddress ? "true" : "false");
            SetInputValue("sequence", parameters.Sequence.ToString(CultureInfo.InvariantCulture));
            SetInputValue("measurementPoint", parameters.MeasurementPoint.ToString(CultureInfo.InvariantCulture));
            GeneratedHex = string.Empty;
            DownstreamApplyStatus =
                $"已回填：行政区划 {parameters.AdministrativeDivision}，终端 {parameters.TerminalAddress}，" +
                $"MSA {parameters.MasterStationAddress}，PN {parameters.MeasurementPoint}，SEQ {parameters.Sequence}。";
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            DownstreamApplyStatus = exception.Message;
        }
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
            SelectedReceiveFilter = IsHeartbeatCommand
                ? ReceiveTrafficFilter.Heartbeat
                : ReceiveTrafficFilter.Business;
            SelectedTabIndex = 1;
        }
        catch (Exception exception)
        {
            AppendReceive(CreateLocalEntry("发送失败", exception.Message));
        }
    }

    /// <summary>
    /// 清空发送页中的生成报文和发送记录。
    /// </summary>
    [RelayCommand]
    private void ClearSend()
    {
        SentText = string.Empty;
        GeneratedHex = string.Empty;
    }

    /// <summary>
    /// 切换接收中心的界面刷新状态；暂停仅影响界面，TCP 接收和文件日志继续运行。
    /// </summary>
    [RelayCommand]
    private void ToggleReceivePause() => IsReceivePaused = !IsReceivePaused;

    /// <summary>
    /// 根据分类按钮参数切换业务、心跳、异常状态或全部记录视图。
    /// </summary>
    /// <param name="filterName">与 <see cref="ReceiveTrafficFilter"/> 枚举名称一致的筛选值。</param>
    [RelayCommand]
    private void SelectReceiveCategory(string? filterName)
    {
        if (Enum.TryParse<ReceiveTrafficFilter>(filterName, true, out var filter))
            SelectedReceiveFilter = filter;
    }

    /// <summary>
    /// 清空当前分类的历史记录；选择“全部”时等同于清空所有接收记录。
    /// </summary>
    [RelayCommand]
    private void ClearCurrentReceiveCategory()
    {
        if (SelectedReceiveFilter == ReceiveTrafficFilter.All)
        {
            ClearReceive();
            return;
        }

        for (var index = _receivedHistory.Count - 1; index >= 0; index--)
        {
            if (MatchesSelectedCategory(_receivedHistory[index]))
                RemoveHistoryEntryAt(index);
        }

        RebuildInstructionFilterOptions();
        RefreshReceivedEntries();
        UpdateFilterLabels();
    }

    /// <summary>
    /// 清空接收页中的文本快照和所有分条通信记录。
    /// </summary>
    [RelayCommand]
    private void ClearReceive()
    {
        ReceivedText = string.Empty;
        ClearPendingTrafficEntries();
        _receivedHistory.Clear();
        ReceivedEntries.Clear();
        _receivedDisplayCharacters = 0;
        _businessMessageCount = 0;
        _heartbeatMessageCount = 0;
        _statusMessageCount = 0;
        _trafficCorrelator.Clear();
        SelectedReceivedEntry = null;
        InstructionFilterOptions.Clear();
        InstructionFilterOptions.Add(AllInstructionFilter);
        SelectedInstructionFilter = AllInstructionFilter;
        UpdateFilterLabels();
        UpdateReceiveViewStatus();
    }

    /// <summary>
    /// 以固定周期持续发送启动时冻结的 376.1 心跳参数，直到被主动停止或取消。
    /// </summary>
    /// <param name="intervalSeconds">相邻两次发送的秒数间隔。</param>
    /// <param name="context">启动时冻结的心跳指令和参数快照。</param>
    /// <param name="cancellationToken">停止定时发送的取消标记。</param>
    private async Task RunAutoHeartbeatLoopAsync(
        int intervalSeconds,
        AutoHeartbeatContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await SendAutoHeartbeatOnceAsync(context, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        AutoHeartbeatStatus = $"发送失败，等待下次重试：{exception.Message}";
                        AppendReceive(CreateLocalEntry("定时心跳发送失败", exception.Message));
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 主动停止定时发送时由取消令牌触发，不向界面输出错误。
        }
        finally
        {
            Dispatcher.UIThread.Post(ResetAutoHeartbeatState);
        }
    }

    /// <summary>
    /// 使用启动时冻结的心跳参数生成一条当前时间的心跳报文并写入 TCP 会话。
    /// </summary>
    /// <param name="context">启动定时心跳时保存的指令和参数快照。</param>
    /// <param name="cancellationToken">发送过程的取消标记。</param>
    private async Task SendAutoHeartbeatOnceAsync(
        AutoHeartbeatContext context,
        CancellationToken cancellationToken)
    {
        // 连接尚未恢复时保持定时器继续运行，等待下一次周期尝试发送。
        if (_session.State != DebugConnectionState.Connected)
            return;

        var result = _encoderRegistry.Build(context.Definition, context.Values);
        await _session.SendAsync(
            result.Bytes,
            context.CommandName,
            result.Summary,
            cancellationToken);
    }

    /// <summary>
    /// 清空定时心跳任务状态；由 UI 线程调用，避免后台任务直接修改可观察属性。
    /// </summary>
    private void ResetAutoHeartbeatState()
    {
        _heartbeatCancellation?.Dispose();
        _heartbeatCancellation = null;
        _heartbeatLoopTask = null;
        IsAutoHeartbeatRunning = false;
    }

    /// <summary>
    /// 按参数键更新当前动态输入区中的值，并保留用户后续手动调整能力。
    /// </summary>
    /// <param name="key">输入参数键。</param>
    /// <param name="value">要写入的文本值。</param>
    private void SetInputValue(string key, string value)
    {
        var input = Inputs.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        if (input is null)
            throw new InvalidOperationException($"抄读回复指令缺少参数“{key}”。");
        input.Value = value;
    }

    /// <summary>
    /// 把当前基础配置和所有协议下最近修改过的参数写入本地配置文件。
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            DebuggerSettingsStore.Save(
                new DebuggerSettings
                {
                    ProtocolName = SelectedProtocolName,
                    Host = Host,
                    Port = Port,
                    HeartbeatIntervalSeconds = HeartbeatIntervalSeconds,
                    InputValues = SnapshotCommandInputMemory()
                });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 退出阶段保存失败不应阻断窗口关闭。
        }
    }

    /// <summary>
    /// 恢复本地配置中保存的协议参数，供后续创建输入项时作为最近一次使用值。
    /// </summary>
    /// <param name="savedValues">按协议名保存的参数键值。</param>
    private void RestoreCommandInputMemory(Dictionary<string, Dictionary<string, string>> savedValues)
    {
        foreach (var protocolPair in savedValues)
        {
            if (Enum.TryParse<DeviceProtocol>(protocolPair.Key, true, out var protocol))
                _commandInputValueMemory.Restore(protocol, protocolPair.Value);
        }
    }

    /// <summary>
    /// 把内存中的协议参数记忆转换为适合 JSON 序列化的字符串键字典。
    /// </summary>
    /// <returns>协议名到参数键值的快照。</returns>
    private Dictionary<string, Dictionary<string, string>> SnapshotCommandInputMemory()
    {
        var snapshot = _commandInputValueMemory.Snapshot();
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var protocolPair in snapshot)
        {
            result.Add(
                protocolPair.Key.ToString(),
                new Dictionary<string, string>(protocolPair.Value, StringComparer.OrdinalIgnoreCase));
        }

        return result;
    }

    /// <summary>
    /// 恢复上次使用的协议，并选中该协议下第一条指令以重建匹配的输入区。
    /// </summary>
    /// <param name="protocolName">配置文件保存的协议显示名称。</param>
    private void ApplySavedProtocol(string protocolName)
    {
        var protocol = protocolName.StartsWith("Q/GDW", StringComparison.OrdinalIgnoreCase)
            ? DeviceProtocol.QGdw3761
            : DeviceProtocol.Dlt6452007;
        SelectedProtocolName = ProtocolResolver.GetDisplayName(protocol);
        var commandNode = FindFirstCommandNode(protocol);
        if (commandNode is not null)
            SelectedNode = commandNode;
    }

    /// <summary>
    /// 在右侧指令树中查找指定协议的第一条可发送指令节点。
    /// </summary>
    /// <param name="protocol">目标协议。</param>
    /// <returns>首个匹配指令节点；不存在时返回 null。</returns>
    private CommandTreeNodeViewModel? FindFirstCommandNode(DeviceProtocol protocol)
    {
        foreach (var commandSet in CommandTree)
        {
            foreach (var group in commandSet.Children)
            {
                foreach (var command in group.Children)
                {
                    if (command.Command?.Protocol == protocol)
                        return command;
                }
            }
        }

        return null;
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

    /// <summary>
    /// 把后台 TCP 会话的状态变化切换到 UI 线程，并刷新连接操作区。
    /// </summary>
    /// <param name="sender">产生状态变化的 TCP 会话。</param>
    /// <param name="eventArgs">新的状态和界面说明。</param>
    private void OnSessionStateChanged(object? sender, DebugConnectionStateChangedEventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionState = eventArgs.State;
            ConnectionStatus = eventArgs.Message;
        });
    }

    /// <summary>
    /// 把后台 TCP 通信记录放入有限队列，由 UI 定时器批量刷新以降低高频心跳造成的界面抖动。
    /// </summary>
    /// <param name="sender">产生通信记录的 TCP 会话。</param>
    /// <param name="entry">待进入界面刷新队列的通信记录。</param>
    private void OnTrafficOccurred(object? sender, DebugTrafficEntry entry)
    {
        lock (_pendingTrafficSync)
        {
            _pendingTrafficEntries.Enqueue(entry);
            while (_pendingTrafficEntries.Count > MaximumPendingTrafficCount)
                _pendingTrafficEntries.Dequeue();
        }
    }

    /// <summary>
    /// 响应 100 毫秒 UI 批量刷新定时器；暂停时只更新等待数量，不消费队列。
    /// </summary>
    /// <param name="sender">Avalonia UI 定时器。</param>
    /// <param name="eventArgs">定时触发参数。</param>
    private void OnTrafficFlushTimerTick(object? sender, EventArgs eventArgs)
    {
        FlushPendingTrafficEntries();
    }

    /// <summary>
    /// 从线程安全队列中提取有限批次并在 UI 线程更新发送记录、关联器和接收中心。
    /// </summary>
    private void FlushPendingTrafficEntries()
    {
        var batch = new List<DebugTrafficEntry>(TrafficFlushBatchSize);
        lock (_pendingTrafficSync)
        {
            if (!IsReceivePaused)
            {
                while (batch.Count < TrafficFlushBatchSize && _pendingTrafficEntries.Count > 0)
                    batch.Add(_pendingTrafficEntries.Dequeue());
            }

            PendingReceiveCount = _pendingTrafficEntries.Count;
        }

        foreach (var entry in batch)
        {
            if (entry.Direction == TrafficDirection.Send)
            {
                _trafficCorrelator.Observe(entry);
                SentText = AppendBounded(SentText, FormatEntry(entry));
            }
            else
            {
                AppendReceive(entry, updateSummary: false);
            }
        }

        if (batch.Count > 0)
        {
            UpdateFilterLabels();
            UpdateReceiveViewStatus();
        }
    }

    /// <summary>
    /// 追加一条接收、状态或错误记录，完成请求关联、分类入库和容量淘汰。
    /// </summary>
    /// <param name="entry">待展示的通信记录。</param>
    /// <param name="updateSummary">是否立即刷新分类数量和状态文字；批量导入时由调用方统一刷新。</param>
    private void AppendReceive(DebugTrafficEntry entry, bool updateSummary = true)
    {
        ReceivedText = AppendBounded(ReceivedText, FormatEntry(entry));
        var correlation = _trafficCorrelator.Observe(entry);
        var item = new ReceivedTrafficItemViewModel(entry, correlation);
        _receivedHistory.Add(item);
        _receivedDisplayCharacters += item.CharacterCount;
        AdjustCategoryCount(item.Category, 1);

        if (!string.Equals(item.InstructionText, "无指令标识", StringComparison.Ordinal) &&
            _instructionFilterValues.Add(item.InstructionText))
        {
            InstructionFilterOptions.Add(item.InstructionText);
        }

        if (MatchesCurrentFilters(item))
        {
            ReceivedEntries.Add(item);
            SelectedReceivedEntry ??= item;
        }

        TrimReceivedHistory();
        if (updateSummary)
        {
            UpdateFilterLabels();
            UpdateReceiveViewStatus();
        }
    }

    /// <summary>
    /// 按总条数、总字符数和心跳独立上限淘汰最早记录，保留业务排障空间。
    /// </summary>
    private void TrimReceivedHistory()
    {
        while (_receivedHistory.Count > MaximumReceivedEntryCount ||
               (_receivedDisplayCharacters > MaximumReceivedDisplayCharacters && _receivedHistory.Count > 1))
        {
            RemoveHistoryEntryAt(0);
        }

        while (_heartbeatMessageCount > MaximumHeartbeatEntryCount)
        {
            var heartbeatIndex = _receivedHistory.FindIndex(
                item => item.Category == DebugTrafficCategory.Heartbeat);
            if (heartbeatIndex < 0)
                break;
            RemoveHistoryEntryAt(heartbeatIndex);
        }
    }

    /// <summary>
    /// 删除指定历史记录并同步可见列表、详情选择、字符计数和分类计数。
    /// </summary>
    /// <param name="index">历史列表中的零基索引。</param>
    private void RemoveHistoryEntryAt(int index)
    {
        var item = _receivedHistory[index];
        _receivedHistory.RemoveAt(index);
        _receivedDisplayCharacters -= item.CharacterCount;
        AdjustCategoryCount(item.Category, -1);
        ReceivedEntries.Remove(item);
        if (ReferenceEquals(SelectedReceivedEntry, item))
            SelectedReceivedEntry = null;
    }

    /// <summary>
    /// 根据当前分类、指令和搜索文字重新构建可见列表。
    /// </summary>
    private void RefreshReceivedEntries()
    {
        var previousSelection = SelectedReceivedEntry;
        ReceivedEntries.Clear();
        foreach (var item in _receivedHistory)
        {
            if (MatchesCurrentFilters(item))
                ReceivedEntries.Add(item);
        }

        SelectedReceivedEntry = previousSelection is not null && ReceivedEntries.Contains(previousSelection)
            ? previousSelection
            : ReceivedEntries.LastOrDefault();
        UpdateReceiveViewStatus();
    }

    /// <summary>
    /// 判断一条记录是否同时满足当前分类、指令和关键字筛选。
    /// </summary>
    /// <param name="item">待判断的接收记录。</param>
    /// <returns>记录应在当前视图显示时返回 true。</returns>
    private bool MatchesCurrentFilters(ReceivedTrafficItemViewModel item)
    {
        if (!MatchesSelectedCategory(item))
            return false;
        if (!string.Equals(SelectedInstructionFilter, AllInstructionFilter, StringComparison.Ordinal) &&
            !string.Equals(item.InstructionText, SelectedInstructionFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return item.MatchesSearch(ReceiveSearchText);
    }

    /// <summary>
    /// 判断一条记录是否属于当前业务、心跳、异常状态或全部分类。
    /// </summary>
    /// <param name="item">待判断的接收记录。</param>
    /// <returns>分类匹配时返回 true。</returns>
    private bool MatchesSelectedCategory(ReceivedTrafficItemViewModel item) => SelectedReceiveFilter switch
    {
        ReceiveTrafficFilter.Business => item.Category == DebugTrafficCategory.Business,
        ReceiveTrafficFilter.Heartbeat => item.Category == DebugTrafficCategory.Heartbeat,
        ReceiveTrafficFilter.Status => item.Category is DebugTrafficCategory.Error or DebugTrafficCategory.System,
        _ => true
    };

    /// <summary>
    /// 按增量更新业务、心跳和异常状态记录数量。
    /// </summary>
    /// <param name="category">待调整的通信分类。</param>
    /// <param name="delta">新增时为 1，删除时为 -1。</param>
    private void AdjustCategoryCount(DebugTrafficCategory category, int delta)
    {
        switch (category)
        {
            case DebugTrafficCategory.Business:
                _businessMessageCount += delta;
                break;
            case DebugTrafficCategory.Heartbeat:
                _heartbeatMessageCount += delta;
                break;
            case DebugTrafficCategory.Error:
            case DebugTrafficCategory.System:
                _statusMessageCount += delta;
                break;
        }
    }

    /// <summary>
    /// 刷新四个分类按钮显示的当前缓存数量。
    /// </summary>
    private void UpdateFilterLabels()
    {
        BusinessFilterLabel = $"业务回复 {_businessMessageCount}";
        HeartbeatFilterLabel = $"心跳 {_heartbeatMessageCount}";
        StatusFilterLabel = $"异常/状态 {_statusMessageCount}";
        AllFilterLabel = $"全部 {_receivedHistory.Count}";
    }

    /// <summary>
    /// 刷新接收中心的可见数量、缓存数量、暂停状态和待刷新数量说明。
    /// </summary>
    private void UpdateReceiveViewStatus()
    {
        var paused = IsReceivePaused ? " · 已暂停" : string.Empty;
        var pending = PendingReceiveCount > 0 ? $" · 待刷新 {PendingReceiveCount}" : string.Empty;
        ReceiveViewStatus = $"显示 {ReceivedEntries.Count} / 缓存 {_receivedHistory.Count}{paused}{pending}";
    }

    /// <summary>
    /// 根据当前保留记录重建指令筛选项，并尽量保留用户之前的选择。
    /// </summary>
    private void RebuildInstructionFilterOptions()
    {
        var previousSelection = SelectedInstructionFilter;
        _instructionFilterValues.Clear();
        InstructionFilterOptions.Clear();
        InstructionFilterOptions.Add(AllInstructionFilter);
        foreach (var item in _receivedHistory)
        {
            if (string.Equals(item.InstructionText, "无指令标识", StringComparison.Ordinal) ||
                !_instructionFilterValues.Add(item.InstructionText))
            {
                continue;
            }

            InstructionFilterOptions.Add(item.InstructionText);
        }

        SelectedInstructionFilter = InstructionFilterOptions.Contains(previousSelection)
            ? previousSelection
            : AllInstructionFilter;
    }

    /// <summary>
    /// 清空尚未批量刷新到界面的通信记录，并同步等待数量。
    /// </summary>
    private void ClearPendingTrafficEntries()
    {
        lock (_pendingTrafficSync)
        {
            _pendingTrafficEntries.Clear();
            PendingReceiveCount = 0;
        }
    }

    /// <summary>
    /// 把通信记录格式化为发送页使用的连续文本。
    /// </summary>
    /// <param name="entry">待格式化的通信记录。</param>
    /// <returns>包含时间、方向、标题、HEX 和详情的多行文本。</returns>
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

    /// <summary>
    /// 创建没有协议原始帧的本地操作异常记录。
    /// </summary>
    /// <param name="title">异常标题。</param>
    /// <param name="details">异常详细说明。</param>
    /// <returns>归类为异常的本地通信记录。</returns>
    private static DebugTrafficEntry CreateLocalEntry(string title, string details) =>
        new(
            DateTimeOffset.Now,
            TrafficDirection.Error,
            title,
            string.Empty,
            details,
            DebugTrafficCategory.Error);

    /// <summary>
    /// 追加连续文本并只保留末尾固定字符数量，限制发送页兼容文本的内存占用。
    /// </summary>
    /// <param name="original">原有文本。</param>
    /// <param name="addition">待追加文本。</param>
    /// <returns>未超过字符上限的文本尾部。</returns>
    private static string AppendBounded(string original, string addition)
    {
        var combined = original + addition;
        return combined.Length <= MaximumTextSnapshotCharacters
            ? combined
            : combined[^MaximumTextSnapshotCharacters..];
    }
}
