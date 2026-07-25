using DeviceSocket.Debugger.Core.CommandSets;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证仓库随程序发布的示例指令集可以通过严格 JSON 和语义校验。
/// </summary>
public sealed class CommandSetLoaderTests
{
    /// <summary>
    /// 验证 376.1 与 645 两个内置文件均能成功加载。
    /// </summary>
    [Fact]
    public async Task BuiltInCommandSets_AreValid()
    {
        var repositoryRoot = FindRepositoryRoot();
        var files = new[]
        {
            Path.Combine(repositoryRoot, "tools", "DeviceSocket.Debugger", "CommandSets", "QGdw3761", "basic-commands.json"),
            Path.Combine(repositoryRoot, "tools", "DeviceSocket.Debugger", "CommandSets", "Dlt645", "basic-commands.json")
        };
        var loader = new CommandSetLoader();

        foreach (var file in files)
        {
            var result = await loader.LoadAsync(file);
            Assert.True(result.IsSuccess, $"{file}: {string.Join("；", result.Errors)}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeviceSocket.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位 DeviceSocket.sln。");
    }
}
