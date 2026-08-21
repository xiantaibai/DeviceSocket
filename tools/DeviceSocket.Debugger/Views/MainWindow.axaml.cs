using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using DeviceSocket.Debugger.ViewModels;

namespace DeviceSocket.Debugger.Views;

/// <summary>
/// 显示连接参数、动态指令输入、收发报文和指令树的主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? _subscribedViewModel;
    private bool _autoScrollScheduled;

    /// <summary>
    /// 初始化主窗口组件。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnWindowDataContextChanged;
        Closed += OnWindowClosed;
    }

    /// <summary>
    /// 在窗口数据上下文变化时切换接收集合订阅，保证自动滚动只绑定当前主视图模型。
    /// </summary>
    /// <param name="sender">主窗口。</param>
    /// <param name="eventArgs">数据上下文变化参数。</param>
    private void OnWindowDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.ReceivedEntries.CollectionChanged -= OnReceivedEntriesChanged;

        _subscribedViewModel = DataContext as MainViewModel;
        if (_subscribedViewModel is not null)
            _subscribedViewModel.ReceivedEntries.CollectionChanged += OnReceivedEntriesChanged;
    }

    /// <summary>
    /// 在可见接收记录变化后合并安排一次滚动，避免批量追加时为每条消息重复滚动。
    /// </summary>
    /// <param name="sender">可见接收记录集合。</param>
    /// <param name="eventArgs">集合变化参数。</param>
    private void OnReceivedEntriesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_subscribedViewModel is not { AutoScrollReceived: true } ||
            _subscribedViewModel.ReceivedEntries.Count == 0 ||
            _autoScrollScheduled)
        {
            return;
        }

        _autoScrollScheduled = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _autoScrollScheduled = false;
                var viewModel = _subscribedViewModel;
                if (viewModel is not { AutoScrollReceived: true } || viewModel.ReceivedEntries.Count == 0)
                    return;

                ReceiveList.ScrollIntoView(viewModel.ReceivedEntries[^1]);
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 窗口关闭时解除集合和窗口事件订阅，避免视图模型被界面事件继续引用。
    /// </summary>
    /// <param name="sender">正在关闭的主窗口。</param>
    /// <param name="eventArgs">窗口关闭参数。</param>
    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.ReceivedEntries.CollectionChanged -= OnReceivedEntriesChanged;
        DataContextChanged -= OnWindowDataContextChanged;
        Closed -= OnWindowClosed;
    }
}
