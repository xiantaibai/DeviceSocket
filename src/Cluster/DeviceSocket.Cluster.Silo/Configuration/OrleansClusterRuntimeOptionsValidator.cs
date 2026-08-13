using System.Net;
using Microsoft.Extensions.Options;

namespace DeviceSocket.Cluster.Silo.Configuration;

/// <summary>
/// 验证 Orleans 集群身份、端口和发布地址。
/// </summary>
public sealed class OrleansClusterRuntimeOptionsValidator : IValidateOptions<OrleansClusterRuntimeOptions>
{
    /// <summary>
    /// 验证集群运行配置。
    /// </summary>
    /// <param name="name">命名选项名称，本配置不区分名称。</param>
    /// <param name="options">待验证配置。</param>
    /// <returns>成功结果或具体配置错误。</returns>
    public ValidateOptionsResult Validate(string? name, OrleansClusterRuntimeOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ClusterId) || string.IsNullOrWhiteSpace(options.ServiceId))
            failures.Add("Orleans:Cluster:ClusterId 和 ServiceId 不能为空。");
        if (options.SiloPort is <= 0 or > 65535 || options.GatewayPort is <= 0 or > 65535)
            failures.Add("Orleans 集群端口必须位于 1..65535。");
        if (!IPAddress.TryParse(options.AdvertisedIPAddress, out _))
            failures.Add("Orleans:Cluster:AdvertisedIPAddress 必须是有效 IP 地址。");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
