using DeviceSocket.Host.Networking.Common;
using Microsoft.Extensions.Configuration;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证两个设备协议 Listener 的强类型配置能够独立绑定。
/// </summary>
public sealed class DeviceTcpListenerOptionsTests
{
    /// <summary>
    /// 验证协议监听端口和连接等待队列不会继续共用根级配置。
    /// </summary>
    [Fact]
    public void Bind_应分别读取两个协议的监听参数()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeviceSocket:Listeners:QGdw3761:Ip"] = "127.0.0.1",
                ["DeviceSocket:Listeners:QGdw3761:Port"] = "15001",
                ["DeviceSocket:Listeners:QGdw3761:BackLog"] = "1024",
                ["DeviceSocket:Listeners:Dlt645:Ip"] = "127.0.0.2",
                ["DeviceSocket:Listeners:Dlt645:Port"] = "15002",
                ["DeviceSocket:Listeners:Dlt645:BackLog"] = "2048"
            })
            .Build();
        var options = new DeviceTcpListenersOptions();

        configuration.GetSection(DeviceTcpListenersOptions.SectionName).Bind(options);

        Assert.Equal("127.0.0.1", options.QGdw3761.Ip);
        Assert.Equal(15001, options.QGdw3761.Port);
        Assert.Equal(1024, options.QGdw3761.BackLog);
        Assert.Equal("127.0.0.2", options.Dlt645.Ip);
        Assert.Equal(15002, options.Dlt645.Port);
        Assert.Equal(2048, options.Dlt645.BackLog);
        Assert.NotSame(options.QGdw3761, options.Dlt645);
    }
}
