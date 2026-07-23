namespace DeviceSocket.Core.Protocols.Common;

/// <summary>
/// 表示拆包器读取一次缓冲区后的结果。
/// </summary>
/// <typeparam name="T">成功拆出的协议包类型。</typeparam>
public readonly record struct FrameReadResult<T>(FrameReadStatus Status, T? Package, int Consumed)
    where T : class
{
    /// <summary>
    /// 返回“数据不足”的结果，此时不能消费缓冲区中的任何字节。
    /// </summary>
    public static FrameReadResult<T> NeedMoreData() => new(FrameReadStatus.NeedMoreData, null, 0);

    /// <summary>
    /// 返回一个已就绪的协议包，包括错误包，并标明应消费的字节数。
    /// </summary>
    public static FrameReadResult<T> Ready(T package, int consumed) =>
        new(FrameReadStatus.PackageReady, package, consumed);
}
