namespace DeviceSocket.Core.Runtime.Messages;

/// <summary>
/// 保存有限数量最近事件的抽象，防止调试数据无限占用内存。
/// </summary>
public interface IRecentStore<T>
{
    /// <summary>
    /// 添加一条事件，容量不足时由实现决定淘汰策略。
    /// </summary>
    void Add(T item);

    /// <summary>
    /// 按从新到旧的顺序返回指定数量事件。
    /// </summary>
    IReadOnlyList<T> GetRecent(int take = 100);
}
