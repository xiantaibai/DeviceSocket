using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace DeviceSocket.Debugger.Services;

/// <summary>
/// 使用 Avalonia StorageProvider 实现指令集文件选择。
/// </summary>
public sealed class AvaloniaCommandSetFilePicker : ICommandSetFilePicker
{
    /// <inheritdoc />
    public async Task<string?> PickAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is null)
            return null;

        var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择协议指令集",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON 指令集")
                    {
                        Patterns = ["*.json"],
                        MimeTypes = ["application/json"]
                    }
                ]
            });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }
}
