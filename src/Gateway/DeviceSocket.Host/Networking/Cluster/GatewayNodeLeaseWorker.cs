using DeviceSocket.Cluster.Contracts.Gateways;
using Microsoft.Extensions.Options;
using Orleans;

namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 定期向 Orleans 续租当前网关节点，供 Cluster Silo 发现准确的 MagicOnion 地址。
/// </summary>
public sealed class GatewayNodeLeaseWorker(
    IClusterClient clusterClient,
    IOptions<GatewayNodeOptions> options,
    ILogger<GatewayNodeLeaseWorker> logger) : BackgroundService
{
    private readonly GatewayNodeOptions _options = options.Value;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// 启动后立即注册，并按配置周期续租；单次失败只记录告警，后台循环继续运行。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_options.NodeHeartbeatSeconds, 1));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                var grain = clusterClient.GetGrain<IGatewayNodeGrain>(_options.NodeId);
                await grain.RegisterOrRenewAsync(new GatewayNodeRegistration
                {
                    InstanceId = _instanceId,
                    RpcEndpoint = _options.RpcEndpoint,
                    SupportedProtocols = ["QGdw3761", "Dlt6452007"],
                    StartedAt = _startedAt,
                    LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(_options.NodeLeaseSeconds)
                });
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "网关节点续租失败: {GatewayNodeId}", _options.NodeId);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
