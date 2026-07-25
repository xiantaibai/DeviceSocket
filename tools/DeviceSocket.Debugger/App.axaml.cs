using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeviceSocket.Debugger.Services;
using DeviceSocket.Debugger.ViewModels;
using DeviceSocket.Debugger.Views;

namespace DeviceSocket.Debugger;

/// <summary>
/// 配置桌面应用生命周期并创建调试工具主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 加载 Avalonia 应用资源。
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 完成桌面生命周期初始化，并在窗口打开后加载内置指令集。
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel(new AvaloniaCommandSetFilePicker());
            var window = new MainWindow { DataContext = viewModel };
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            window.Closed += async (_, _) => await viewModel.DisposeAsync();
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
