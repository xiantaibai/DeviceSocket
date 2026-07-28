using System.Collections.Concurrent;
using DeviceSocket.Rpc.Contracts.Gateway;

namespace DeviceSocket.Host.Rpc;

/// <summary>
/// 在网关进程内按 MsgId 和 AttemptNo 合并并发重试，防止 RPC 超时后重复写入设备 Socket。
/// </summary>
public sealed class GatewayCommandDeduplicator
{
    private const int MaximumEntries = 100_000;
    private readonly ConcurrentDictionary<string, Lazy<Task<SendCommandResponse>>> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    /// <summary>
    /// 首次调用执行发送工厂；相同 MsgId 和 AttemptNo 的后续调用等待并复用首次结果。
    /// </summary>
    public async Task<SendCommandResponse> ExecuteAsync(
        string msgId,
        int attemptNo,
        Func<Task<SendCommandResponse>> sendFactory)
    {
        var deduplicationKey = $"{msgId}:{attemptNo}";
        var candidate = new Lazy<Task<SendCommandResponse>>(sendFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        var actual = _entries.GetOrAdd(deduplicationKey, candidate);
        if (ReferenceEquals(candidate, actual))
        {
            _insertionOrder.Enqueue(deduplicationKey);
            TrimIfNeeded();
        }

        return await actual.Value;
    }

    private void TrimIfNeeded()
    {
        var maximumInspections = _entries.Count;
        var inspected = 0;
        while (_entries.Count > MaximumEntries &&
               inspected++ < maximumInspections &&
               _insertionOrder.TryDequeue(out var oldest))
        {
            if (_entries.TryGetValue(oldest, out var entry) &&
                entry.IsValueCreated &&
                entry.Value.IsCompleted)
            {
                _entries.TryRemove(oldest, out _);
            }
            else
            {
                // 正在执行的发送不能被清理，否则同一 MsgId 的并发重试可能再次写入 Socket。
                _insertionOrder.Enqueue(oldest);
            }
        }
    }
}
