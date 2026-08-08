using DeviceSocket.LoadTester.Configuration;

namespace DeviceSocket.LoadTester.CommandLine;

internal abstract record LoadTesterCommand;

internal sealed record RunLoadTesterCommand(string ConfigurationPath, RunCommandOverrides Overrides) : LoadTesterCommand;

internal sealed record MergeReportCommand(string InputDirectory, string OutputDirectory) : LoadTesterCommand;

internal sealed record HelpCommand : LoadTesterCommand;
