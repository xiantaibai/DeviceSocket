using Microsoft.Extensions.Hosting;

namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 为 SuperSocket 子宿主提供空生命周期实现；Ctrl+C 和进程停止信号只由外层 ASP.NET Core 宿主管理。
/// </summary>
public sealed class NestedHostLifetime : IHostLifetime
{
    /// <summary>
    /// 子宿主无需等待独立的启动信号，立即完成启动等待。
    /// </summary>
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 子宿主不注册独立的进程停止处理，立即完成停止通知。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
