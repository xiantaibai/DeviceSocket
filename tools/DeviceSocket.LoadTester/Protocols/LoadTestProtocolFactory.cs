using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Protocols.Dlt645;
using DeviceSocket.LoadTester.Protocols.QGdw3761;

namespace DeviceSocket.LoadTester.Protocols;

/// <summary>
/// 根据通信协议和服务端兼容配置创建相互隔离的负载测试协议适配器。
/// </summary>
internal static class LoadTestProtocolFactory
{
    /// <summary>
    /// 按协议创建使用当前 DeviceSocket 确认规则的默认适配器。
    /// </summary>
    /// <param name="protocol">虚拟设备使用的通信协议。</param>
    /// <returns>对应协议的默认负载测试适配器。</returns>
    internal static ILoadTestProtocol Create(LoadProtocol protocol) => Create(
        new TargetOptions { Protocol = protocol },
        new GatherReadingResponseOptions());

    /// <summary>
    /// 按完整负载配置创建协议适配器，使 Gather 读表业务响应使用当前场景参数。
    /// </summary>
    /// <param name="options">已经通过校验的完整负载配置。</param>
    /// <returns>同时匹配目标协议、兼容语义和业务应答模型的适配器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为空时抛出。</exception>
    internal static ILoadTestProtocol Create(LoadTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(options.Target, options.Workload.GatherReadingResponse);
    }

    /// <summary>
    /// 按通信协议和服务端兼容配置创建隔离的负载测试适配器。
    /// </summary>
    /// <param name="target">已经通过配置校验的目标服务端选项。</param>
    /// <returns>同时匹配协议格式和服务端确认语义的适配器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> 为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">协议与兼容配置组合不受支持时抛出。</exception>
    internal static ILoadTestProtocol Create(TargetOptions target) =>
        Create(target, new GatherReadingResponseOptions());

    /// <summary>
    /// 按目标服务配置和 Gather 读表响应参数创建隔离的协议适配器。
    /// </summary>
    /// <param name="target">已经通过配置校验的目标服务端选项。</param>
    /// <param name="readingResponse">Gather AFN=0CH/F129 业务响应配置。</param>
    /// <returns>可供虚拟会话使用的协议适配器。</returns>
    /// <exception cref="ArgumentNullException">任一配置为空时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">协议与兼容配置组合不受支持时抛出。</exception>
    private static ILoadTestProtocol Create(
        TargetOptions target,
        GatherReadingResponseOptions readingResponse)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(readingResponse);
        return (target.Protocol, target.CompatibilityProfile) switch
        {
            (LoadProtocol.QGdw3761, ServerCompatibilityProfile.DeviceSocket) => new QGdw3761LoadTestProtocol(),
            (LoadProtocol.QGdw3761, ServerCompatibilityProfile.GatherTcpCenter) => new GatherTcpCenterQGdw3761LoadTestProtocol(readingResponse),
            (LoadProtocol.Dlt6452007, ServerCompatibilityProfile.DeviceSocket) => new Dlt645LoadTestProtocol(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), "通信协议与服务端兼容配置的组合不受支持。")
        };
    }
}
