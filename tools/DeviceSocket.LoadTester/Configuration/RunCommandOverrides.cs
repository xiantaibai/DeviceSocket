namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 表示命令行对场景文件中分布式运行和输出位置的覆盖值。
/// </summary>
/// <param name="RunId">覆盖 <c>distributed.runId</c>；空表示保留配置文件值，多个分片应使用同一非空编号。</param>
/// <param name="ShardIndex">覆盖 <c>distributed.shardIndex</c>；空表示保留配置文件值，非空时为零基分片序号。</param>
/// <param name="ShardCount">覆盖 <c>distributed.shardCount</c>；空表示保留配置文件值，非空时必须大于零。</param>
/// <param name="StartAtUtc">覆盖 <c>distributed.startAtUtc</c>；空表示保留配置文件值，非空时用于多机同步启动。</param>
/// <param name="OutputDirectory">覆盖 <c>reporting.outputDirectory</c>；空表示保留配置文件值。</param>
/// <param name="Role">覆盖 role；devicesOnly 同时关闭 HTTP 发压，供业务页面手动下发并由设备自动确认。</param>
public sealed record RunCommandOverrides(
    string? RunId = null,
    int? ShardIndex = null,
    int? ShardCount = null,
    DateTimeOffset? StartAtUtc = null,
    string? OutputDirectory = null,
    LoadTestRole? Role = null);
