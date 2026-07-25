using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.CommandSets.Models;
using DeviceSocket.Debugger.Core.Encoding;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证 JSON 指令参数能够通过安全编码器生成协议标准帧。
/// </summary>
public sealed class CommandEncoderRegistryTests
{
    /// <summary>
    /// 验证 376.1 登录指令自动计算双长度字段和校验和。
    /// </summary>
    [Fact]
    public void QGdw3761Login_BuildsExpectedFrame()
    {
        var command = new CommandDefinition
        {
            Id = "login",
            Name = "登录",
            Encoder = "qgdw3761.link",
            FixedValues = new Dictionary<string, string> { ["linkKind"] = "Login" }
        };
        var values = new Dictionary<string, string>
        {
            ["administrativeDivision"] = "0000",
            ["terminalAddress"] = "0",
            ["sequence"] = "1"
        };

        var result = new CommandEncoderRegistry().Build(command, values);

        Assert.Equal(
            "68 32 00 32 00 68 C9 00 00 00 00 00 02 61 00 00 01 00 2D 16",
            HexCodec.ToHex(result.Bytes, true));
    }

    /// <summary>
    /// 验证 645 编码器完成地址倒序、数据域加 33H、长度和校验和计算。
    /// </summary>
    [Fact]
    public void Dlt645ReadData_BuildsExpectedFrame()
    {
        var command = new CommandDefinition
        {
            Id = "read",
            Name = "读取数据",
            Encoder = "dlt645.frame",
            FixedValues = new Dictionary<string, string>
            {
                ["control"] = "0x11",
                ["preambleLength"] = "4"
            }
        };
        var values = new Dictionary<string, string>
        {
            ["address"] = "123456789012",
            ["dataHex"] = "00 00 01 00"
        };

        var result = new CommandEncoderRegistry().Build(command, values);

        Assert.Equal(
            "FE FE FE FE 68 12 90 78 56 34 12 68 11 04 33 33 34 33 68 16",
            HexCodec.ToHex(result.Bytes, true));
    }

    /// <summary>
    /// 验证 raw 模式仅替换显式占位符并拒绝遗留表达式。
    /// </summary>
    [Fact]
    public void RawMode_ReplacesDeclaredPlaceholder()
    {
        var command = new CommandDefinition
        {
            Id = "raw",
            Name = "原始报文",
            Mode = "raw",
            RawHex = "68 {{payload}} 16",
            Inputs = [new CommandInputDefinition { Key = "payload", Label = "内容" }]
        };

        var result = new CommandEncoderRegistry().Build(
            command,
            new Dictionary<string, string> { ["payload"] = "01 02" });

        Assert.Equal("68 01 02 16", HexCodec.ToHex(result.Bytes, true));
    }

    /// <summary>
    /// 验证 layout 模式可以仅靠 JSON 等价模型完成 645 长度、加 33H 和校验和处理。
    /// </summary>
    [Fact]
    public void LayoutMode_ComputesDlt645LengthAndChecksum()
    {
        var command = new CommandDefinition
        {
            Id = "layout",
            Name = "声明式读取",
            Mode = "layout",
            Layout = new CommandLayoutDefinition
            {
                Fields =
                [
                    new() { Name = "start", Source = "fixed", Value = "68" },
                    new() { Name = "address", Source = "input", InputKey = "address", Encoding = "bcdReverse" },
                    new() { Name = "start2", Source = "fixed", Value = "68" },
                    new() { Name = "control", Source = "fixed", Value = "11" },
                    new()
                    {
                        Name = "length",
                        Source = "computed",
                        Algorithm = "byteLength",
                        FromField = "data",
                        Size = 1
                    },
                    new() { Name = "data", Source = "input", InputKey = "data", Encoding = "hexAdd33" },
                    new()
                    {
                        Name = "checksum",
                        Source = "computed",
                        Algorithm = "sum8",
                        FromField = "start",
                        ToField = "data",
                        Size = 1
                    },
                    new() { Name = "end", Source = "fixed", Value = "16" }
                ]
            }
        };

        var result = new CommandEncoderRegistry().Build(
            command,
            new Dictionary<string, string>
            {
                ["address"] = "123456789012",
                ["data"] = "00 00 01 00"
            });

        Assert.Equal(
            "68 12 90 78 56 34 12 68 11 04 33 33 34 33 68 16",
            HexCodec.ToHex(result.Bytes, true));
    }
}
