using System.Collections.Concurrent;
using DeviceSocket.Core.Runtime.Messages;

namespace DeviceSocket.Infrastructure.Memory.Messages;

/// <summary>
/// 基于并发队列的有界最近事件存储，容量超限时淘汰最旧记录。
/// </summary>
public sealed class MemoryRecentStore<T>(int capacity = 1000) : IRecentStore<T>
{
    private readonly ConcurrentQueue<T> _items = new();
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private int _count;

    /// <summary>
    /// 添加事件并立即清理超出容量的最旧项目。
    /// </summary>
    public void Add(T item)
    {
        _items.Enqueue(item);
        Interlocked.Increment(ref _count);

        while (Volatile.Read(ref _count) > _capacity && _items.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    /// <summary>
    /// 按从新到旧顺序返回最近事件，返回数量不超过配置容量。
    /// </summary>
    public IReadOnlyList<T> GetRecent(int take = 100)
    {
        take = Math.Clamp(take, 1, _capacity);
        return _items.Reverse().Take(take).ToArray();
    }
}
