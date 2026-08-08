namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示命令行对场景文件中分布式运行和输出位置的覆盖值。
/// </summary>
public sealed record RunCommandOverrides(
    string? RunId = null,
    int? ShardIndex = null,
    int? ShardCount = null,
    DateTimeOffset? StartAtUtc = null,
    string? OutputDirectory = null);
