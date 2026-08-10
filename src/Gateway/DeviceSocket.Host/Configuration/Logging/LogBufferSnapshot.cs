namespace DeviceSocket.Host.Configuration.Logging;

/// <summary>
/// 表示一个 Serilog 异步文件流在采样时刻的积压、容量和累计丢弃数量。
/// </summary>
/// <param name="LogType">日志流稳定名称。</param>
/// <param name="Count">当前等待写入的事件数。</param>
/// <param name="BufferSize">异步缓冲总容量。</param>
/// <param name="DroppedMessagesCount">缓冲溢出后的累计丢弃数。</param>
public sealed record LogBufferSnapshot(
    string LogType,
    int Count,
    int BufferSize,
    long DroppedMessagesCount);
