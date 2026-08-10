using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Runtime.Messages;

namespace DeviceSocket.Host.Observability;

/// <summary>
/// 使用原子计数记录设备通讯日志提交、同步失败以及心跳过滤结果。
/// </summary>
public sealed class DeviceTrafficLoggingMetrics
{
    private long _upstreamSubmitted;
    private long _downstreamSubmitted;
    private long _synchronousWriteFailures;
    private long _heartbeatSuppressed;
    private long _heartbeatRecordedByDevice;
    private long _heartbeatRecordedBySampling;

    /// <summary>进程启动以来提交给上行异步日志 Sink 的记录数。</summary>
    public long UpstreamSubmitted => Interlocked.Read(ref _upstreamSubmitted);

    /// <summary>进程启动以来提交给下发异步日志 Sink 的记录数。</summary>
    public long DownstreamSubmitted => Interlocked.Read(ref _downstreamSubmitted);

    /// <summary>进入异步队列前由日志提供程序同步抛出的累计失败数。</summary>
    public long SynchronousWriteFailures => Interlocked.Read(ref _synchronousWriteFailures);

    /// <summary>进程启动以来在生成 RawHex 前被策略过滤的上行心跳数。</summary>
    public long HeartbeatSuppressed => Interlocked.Read(ref _heartbeatSuppressed);

    /// <summary>进程启动以来因设备白名单而完整记录的上行心跳数。</summary>
    public long HeartbeatRecordedByDevice => Interlocked.Read(ref _heartbeatRecordedByDevice);

    /// <summary>进程启动以来因确定性采样命中而记录的上行心跳数。</summary>
    public long HeartbeatRecordedBySampling => Interlocked.Read(ref _heartbeatRecordedBySampling);

    /// <summary>
    /// 记录一条已经成功提交给对应方向日志管道的通讯事件。
    /// </summary>
    /// <param name="direction">通讯方向。</param>
    public void TrafficSubmitted(MessageDirection direction)
    {
        if (direction == MessageDirection.Upstream)
            Interlocked.Increment(ref _upstreamSubmitted);
        else
            Interlocked.Increment(ref _downstreamSubmitted);
    }

    /// <summary>记录一次通讯日志同步提交失败。</summary>
    public void SynchronousWriteFailed() => Interlocked.Increment(ref _synchronousWriteFailures);

    /// <summary>
    /// 记录一条心跳日志策略判定结果。
    /// </summary>
    /// <param name="result">心跳日志判定结果。</param>
    public void HeartbeatEvaluated(HeartbeatLogDecision result)
    {
        switch (result)
        {
            case HeartbeatLogDecision.Suppressed:
                Interlocked.Increment(ref _heartbeatSuppressed);
                break;
            case HeartbeatLogDecision.DeviceEnabled:
                Interlocked.Increment(ref _heartbeatRecordedByDevice);
                break;
            case HeartbeatLogDecision.Sampled:
                Interlocked.Increment(ref _heartbeatRecordedBySampling);
                break;
        }
    }
}
