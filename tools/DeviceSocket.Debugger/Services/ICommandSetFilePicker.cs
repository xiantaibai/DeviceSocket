namespace DeviceSocket.Debugger.Services;

/// <summary>
/// 抽象跨平台指令集文件选择行为，避免视图模型直接依赖窗口对象。
/// </summary>
public interface ICommandSetFilePicker
{
    /// <summary>
    /// 打开 JSON 文件选择器。
    /// </summary>
    /// <returns>用户选中的本地文件路径；取消时返回空。</returns>
    Task<string?> PickAsync();
}
