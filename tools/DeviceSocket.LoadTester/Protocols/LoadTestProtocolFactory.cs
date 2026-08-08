using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Protocols.Dlt645;
using DeviceSocket.LoadTester.Protocols.QGdw3761;

namespace DeviceSocket.LoadTester.Protocols;

internal static class LoadTestProtocolFactory
{
    internal static ILoadTestProtocol Create(LoadProtocol protocol) => protocol switch
    {
        LoadProtocol.QGdw3761 => new QGdw3761LoadTestProtocol(),
        LoadProtocol.Dlt6452007 => new Dlt645LoadTestProtocol(),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };
}
