namespace DeviceSocket.LoadTester.Runtime;

internal sealed class DeviceTimeoutException(DeviceTimeoutKind kind, string message) : TimeoutException(message)
{
    public DeviceTimeoutKind Kind { get; } = kind;
}
