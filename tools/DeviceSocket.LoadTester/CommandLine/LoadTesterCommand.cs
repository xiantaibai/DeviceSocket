using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Reporting.Detailed;

namespace DeviceSocket.LoadTester.CommandLine;

/// <summary>
/// 表示 LoadTester 支持的强类型命令基类，用于穷尽式分派运行、报告合并、详细报告生成和帮助操作。
/// </summary>
internal abstract record LoadTesterCommand;

/// <summary>
/// 表示加载一个场景文件并执行单节点或分片负载测试的命令。
/// </summary>
/// <param name="ConfigurationPath">场景 JSON 文件路径。</param>
/// <param name="Overrides">命令行对运行编号、分片和输出路径的可选覆盖值。</param>
internal sealed record RunLoadTesterCommand(string ConfigurationPath, RunCommandOverrides Overrides) : LoadTesterCommand;

/// <summary>
/// 表示读取同一运行编号下的节点报告并生成集群级汇总报告的命令。
/// </summary>
/// <param name="InputDirectory">包含各分片 <c>node-summary.json</c> 的输入目录。</param>
/// <param name="OutputDirectory">写入聚合 JSON 和 Markdown 报告的目录。</param>
internal sealed record MergeReportCommand(string InputDirectory, string OutputDirectory) : LoadTesterCommand;

/// <summary>
/// 表示读取既有负载测试结果和可选 TcpCenter 指标并生成面向验收或排障人员的离线详细报告。
/// </summary>
/// <param name="Options">报告输入、受众、阈值、元数据和可选基线对比参数。</param>
internal sealed record GenerateDetailedReportCommand(DetailedReportGenerationOptions Options) : LoadTesterCommand;

/// <summary>
/// 表示输出命令用法且不执行负载的帮助命令。
/// </summary>
internal sealed record HelpCommand : LoadTesterCommand;
