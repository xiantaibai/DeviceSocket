namespace DeviceSocket.LoadTester.Reporting.Detailed;

/// <summary>
/// 表示 <c>report generate</c> 离线报告命令的完整参数；这些参数只影响报告解释和展示，不会重新发起压测或修改源结果。
/// </summary>
internal sealed record DetailedReportGenerationOptions
{
    /// <summary>获取必填的 LoadTester 结果目录，可以是单个 <c>shard-xxx</c> 目录或包含多个分片的运行根目录。</summary>
    internal required string InputDirectory { get; init; }

    /// <summary>获取可选服务端指标；旧 Gather 使用自定义 JSONL，GatherV4 使用 Collector OTLP JSON/JSONL；V4 必须显式指定并与压测窗口重叠。</summary>
    internal string? ServerMetricsPath { get; init; }

    /// <summary>获取可选的测试背景 JSON 路径；为空时从节点报告自动推断场景信息，其余服务器和代码信息显示未填写。</summary>
    internal string? BackgroundPath { get; init; }

    /// <summary>获取报告输出目录；默认是输入目录下的 <c>analysis</c>，源报告不会被覆盖。</summary>
    internal required string OutputDirectory { get; init; }

    /// <summary>获取目标受众，默认同时生成验收版和排障版。</summary>
    internal DetailedReportAudience Audience { get; init; } = DetailedReportAudience.Both;

    /// <summary>获取可选基线 LoadTester 结果目录；设置后输出改造前后关键指标差异。</summary>
    internal string? BaselineInputDirectory { get; init; }

    /// <summary>获取可选基线 Gather.TcpCenter JSONL 指标文件；仅在设置基线结果时使用。</summary>
    internal string? BaselineServerMetricsPath { get; init; }

    /// <summary>获取报告标题；为空时根据 RunId 自动生成。</summary>
    internal string? Title { get; init; }

    /// <summary>获取部署环境说明，例如“公司测试环境”或“正式环境灰度节点”；为空时报告显示未填写。</summary>
    internal string? Environment { get; init; }

    /// <summary>获取被测服务版本或分支说明；为空时报告显示未填写。</summary>
    internal string? ServiceVersion { get; init; }

    /// <summary>获取执行或整理报告的人员说明；为空时报告显示未填写。</summary>
    internal string? Tester { get; init; }

    /// <summary>获取服务端无时区时间戳使用的系统时区标识；默认 <c>Local</c> 表示报告机本地时区。</summary>
    internal string ServerTimeZoneId { get; init; } = "Local";

    /// <summary>获取业务在线从物理连接目标追平的最大允许秒数，默认 60 秒；超过时验收失败并生成登录慢路径问题。</summary>
    internal int MaximumBusinessCatchupSeconds { get; init; } = 60;

    /// <summary>获取 TcpCenter 接收业务管线允许的最大排队年龄，默认 30 秒；服务指标缺失时该规则标记为不可评估。</summary>
    internal int MaximumQueueAgeSeconds { get; init; } = 30;

    /// <summary>获取 TcpCenter ACK 组帧到 Socket Send 返回的最大允许耗时，默认 5000 毫秒。</summary>
    internal int MaximumAckMilliseconds { get; init; } = 5000;

    /// <summary>获取可选目标设备数覆盖值；为空时从节点报告的全局设备配置和观测峰值推断。</summary>
    internal int? TargetDeviceCount { get; init; }
}
