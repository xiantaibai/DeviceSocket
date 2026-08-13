namespace DeviceSocket.Cluster.Persistence.Configuration;

/// <summary>
/// 每种应用 Grain 类型的独立关系表配置，对应 <c>Orleans:Persistence:Tables</c>。
/// </summary>
public sealed class RelationalGrainTableOptions
{
    /// <summary>DeviceGrain 状态表名，默认 <c>OrleansDeviceState</c>。</summary>
    public string Device { get; set; } = "OrleansDeviceState";

    /// <summary>DeviceCommandGrain 状态表名，默认 <c>OrleansDeviceCommandState</c>。</summary>
    public string Command { get; set; } = "OrleansDeviceCommandState";

    /// <summary>GatewayNodeGrain 状态表名，默认 <c>OrleansGatewayNodeState</c>。</summary>
    public string Gateway { get; set; } = "OrleansGatewayNodeState";

    /// <summary>DeviceCommandSchedulerGrain 状态表名，默认 <c>OrleansDeviceCommandSchedulerState</c>。</summary>
    public string Scheduler { get; set; } = "OrleansDeviceCommandSchedulerState";
}
