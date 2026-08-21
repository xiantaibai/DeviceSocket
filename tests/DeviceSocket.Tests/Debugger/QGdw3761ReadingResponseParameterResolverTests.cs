using DeviceSocket.Debugger.Core.Protocols.QGdw3761;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证抄读回复界面能够从主站下发报文中解析出地址、MSA、PN 和 RSEQ 回填参数。
/// </summary>
public sealed class QGdw3761ReadingResponseParameterResolverTests
{
    /// <summary>
    /// 验证合法 AFN=0CH/F129 下行报文的地址、MSA、群地址、PN 和 SEQ 解析正确。
    /// </summary>
    [Fact]
    public void Resolve_ValidReadingRequest_ReturnsExpectedParameters()
    {
        const string requestHex =
            "68 32 00 32 00 68 4B 10 32 AC 1C 54 0C 62 08 01 01 10 31 16";

        var parameters = QGdw3761ReadingResponseParameterResolver.Resolve(requestHex);

        Assert.Equal("3210", parameters.AdministrativeDivision);
        Assert.Equal(7340, parameters.TerminalAddress);
        Assert.Equal(42, parameters.MasterStationAddress);
        Assert.False(parameters.GroupAddress);
        Assert.Equal(2, parameters.Sequence);
        Assert.Equal(4, parameters.MeasurementPoint);
    }

    /// <summary>
    /// 验证校验和错误的下行报文不会进入参数回填流程。
    /// </summary>
    [Fact]
    public void Resolve_InvalidChecksum_ThrowsFormatException()
    {
        const string requestHex =
            "68 32 00 32 00 68 4B 10 32 AC 1C 54 0C 62 08 01 01 10 31 15";

        var exception = Assert.Throws<FormatException>(
            () => QGdw3761ReadingResponseParameterResolver.Resolve(requestHex));

        Assert.Contains("校验失败", exception.Message, StringComparison.Ordinal);
    }
}
