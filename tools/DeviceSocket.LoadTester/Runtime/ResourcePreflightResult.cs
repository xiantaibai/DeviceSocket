namespace DeviceSocket.LoadTester.Runtime;

internal sealed record ResourcePreflightResult(
    IReadOnlyList<string> Facts,
    IReadOnlyList<string> Warnings);
