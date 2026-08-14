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

        SendCommandResponse response;
        try
        {
            response = await actual.Value;
        }
        catch
        {
            // 异常任务也不能永久占用幂等键；并发调用仍共享本次异常，之后同 AttemptNo 可以按相同协议事务重试。
            ((ICollection<KeyValuePair<string, Lazy<Task<SendCommandResponse>>>>)_entries)
                .Remove(new KeyValuePair<string, Lazy<Task<SendCommandResponse>>>(deduplicationKey, actual));
            throw;
        }
        if (response.Status != GatewayDispatchStatus.WrittenToSocket)
        {
            // 失败结果只合并本轮并发调用；后续同 AttemptNo 必须能够再次执行发送，并由协议层复用 PSEQ 保证终端幂等。
            ((ICollection<KeyValuePair<string, Lazy<Task<SendCommandResponse>>>>)_entries)
                .Remove(new KeyValuePair<string, Lazy<Task<SendCommandResponse>>>(deduplicationKey, actual));
            return response;
        }

        if (ReferenceEquals(candidate, actual))
            return response;

        // 同一 AttemptNo 的 RPC 重放不能再次写 Socket，但仍要显式返回 Duplicate，避免 Silo 把缓存的失败结果误认为“本次又执行了一次发送”。
        return new SendCommandResponse
        {
            MsgId = response.MsgId,
            Status = GatewayDispatchStatus.Duplicate,
            Message = "相同 MsgId 和 AttemptNo 已处理，本次未重复写入设备 Socket。",
            BytesWritten = response.BytesWritten,
            GatewayNodeId = response.GatewayNodeId,
            OccurredAt = response.OccurredAt
        };
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
