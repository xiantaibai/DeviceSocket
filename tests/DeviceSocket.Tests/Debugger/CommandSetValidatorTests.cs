using DeviceSocket.Debugger.Core.CommandSets;
using DeviceSocket.Debugger.Core.CommandSets.Models;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证错误指令文件可以在进入编解码阶段前被精确拦截。
/// </summary>
public sealed class CommandSetValidatorTests
{
    /// <summary>
    /// 验证重复指令标识和未知输入类型会同时返回，而不是中断程序。
    /// </summary>
    [Fact]
    public void InvalidCommands_ReturnAllSemanticErrors()
    {
        var definition = new CommandSetDefinition
        {
            Name = "测试",
            Protocol = "QGdw3761",
            DefaultPort = 5001,
            Groups =
            [
                new CommandGroupDefinition
                {
                    Name = "分组",
                    Commands =
                    [
                        new CommandDefinition { Id = "same", Name = "一", Encoder = "qgdw3761.link" },
                        new CommandDefinition
                        {
                            Id = "same",
                            Name = "二",
                            Encoder = "qgdw3761.link",
                            Inputs = [new CommandInputDefinition { Key = "x", Label = "X", Type = "script" }]
                        }
                    ]
                }
            ]
        };

        var errors = CommandSetValidator.Validate(definition);

        Assert.Contains(errors, error => error.Contains("重复", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("不支持输入类型", StringComparison.Ordinal));
    }
}
