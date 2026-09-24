using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.Workloads.Gather;

/// <summary>按服务端兼容配置创建互不影响的 Gather HTTP 指令适配器。</summary>
internal static class CommandWorkloadAdapterFactory
{
    /// <summary>创建与当前目标服务端响应语义一致的适配器。</summary>
    /// <param name="options">已经完成基础校验的完整场景配置。</param>
    /// <returns>Gather.TcpCenter 或 GatherV4 指令适配器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">兼容配置没有 HTTP 指令实现时抛出。</exception>
    internal static ICommandWorkloadAdapter Create(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Target.CompatibilityProfile switch
        {
            ServerCompatibilityProfile.GatherTcpCenter => new GatherTcpCenterCommandWorkloadAdapter(options.CommandWorkload),
            ServerCompatibilityProfile.GatherV4 => new GatherV4CommandWorkloadAdapter(options.CommandWorkload.GatherV4),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "当前服务端兼容配置不支持 Gather HTTP 指令负载。")
        };
    }
}
