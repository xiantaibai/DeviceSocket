using Avalonia;

namespace DeviceSocket.Debugger;

/// <summary>
/// 提供 DeviceSocket 协议调试工具的桌面启动入口。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 启动 Avalonia 经典桌面应用生命周期。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>
    /// 创建供运行时和设计器共同使用的 Avalonia 应用构建器。
    /// </summary>
    /// <returns>已经完成平台和字体配置的构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
