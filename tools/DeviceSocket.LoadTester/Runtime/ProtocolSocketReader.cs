using System.Buffers;
using System.Net.Sockets;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.LoadTester.Protocols;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 维护单条 TCP 会话的可复用接收缓冲区，循环调用协议拆包器处理半包和粘包，并对连续未成帧数据设置内存上限。
/// </summary>
/// <param name="protocol">当前虚拟设备使用的协议编解码适配器。</param>
internal sealed class ProtocolSocketReader(ILoadTestProtocol protocol) : IDisposable
{
    private const int InitialBufferSize = 512;
    private const int MaximumBufferSize = 1024 * 1024;
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    private int _count;
    private bool _disposed;

    internal Task<int> StartReadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        EnsureWritableCapacity();
        return stream.ReadAsync(_buffer.AsMemory(_count, _buffer.Length - _count), cancellationToken).AsTask();
    }

    internal IReadOnlyList<IProtocolFrame> CompleteRead(int bytesRead)
    {
        if (bytesRead < 0 || _count + bytesRead > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(bytesRead));
        _count += bytesRead;
        var frames = new List<IProtocolFrame>();

        while (_count > 0)
        {
            var result = protocol.Read(_buffer.AsSpan(0, _count));
            if (result.Status == FrameReadStatus.NeedMoreData)
                break;
            if (result.Consumed <= 0 || result.Consumed > _count)
                throw new InvalidDataException("协议解析器返回了无效的消费字节数。");

            if (result.Frame is not null)
                frames.Add(result.Frame);
            var remaining = _count - result.Consumed;
            if (remaining > 0)
                Buffer.BlockCopy(_buffer, result.Consumed, _buffer, 0, remaining);
            _count = remaining;
        }

        return frames;
    }

    void IDisposable.Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _count = 0;
    }

    private void EnsureWritableCapacity()
    {
        if (_count < _buffer.Length)
            return;
        if (_buffer.Length >= MaximumBufferSize)
            throw new InvalidDataException("连续未成帧数据超过 1 MiB，已终止当前设备连接。");

        var replacement = ArrayPool<byte>.Shared.Rent(Math.Min(_buffer.Length * 2, MaximumBufferSize));
        Buffer.BlockCopy(_buffer, 0, replacement, 0, _count);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = replacement;
    }
}
