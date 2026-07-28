namespace DeviceSocket.Host.Networking.Common;

/// <summary>
/// 通过异步调用上下文把跨服务 MsgId 传递到实际 Socket 写入层，未设置时由连接层自动生成。
/// </summary>
internal static class DeviceSendAuditContext
{
    private static readonly AsyncLocal<AuditState?> CurrentState = new();

    /// <summary>取得当前下发调用的审计信息。</summary>
    public static AuditState? Current => CurrentState.Value;

    /// <summary>
    /// 为当前异步调用链设置审计信息，释放返回对象后恢复进入前的上下文。
    /// </summary>
    public static IDisposable Push(string msgId, string? traceId, string source, int? attemptNo = null)
    {
        var previous = CurrentState.Value;
        CurrentState.Value = new AuditState(msgId, traceId, source, attemptNo);
        return new RestoreScope(previous);
    }

    /// <summary>单次 Socket 写入可读取的审计字段。</summary>
    internal sealed record AuditState(string MsgId, string? TraceId, string Source, int? AttemptNo);

    private sealed class RestoreScope(AuditState? previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}
