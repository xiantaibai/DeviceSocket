using System.Net.Sockets;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Debugger.Core.Decoding;

namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 维护一个异步 TCP 调试连接，并持续拆分和解析接收到的协议帧。
/// </summary>
public sealed class DebugTcpSession : IAsyncDisposable
{
    private const int ReceiveChunkSize = 8192;
    private const int MaximumBufferedBytes = 1024 * 1024;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TrafficFileLogger _fileLogger;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveTask;
    private DebugConnectionState _state = DebugConnectionState.Disconnected;

    /// <summary>
    /// 初始化 TCP 调试会话，并指定报文日志目录。
    /// </summary>
    /// <param name="logDirectory">发送、接收日志的保存目录。</param>
    public DebugTcpSession(string logDirectory)
    {
        _fileLogger = new TrafficFileLogger(logDirectory);
    }

    /// <summary>当连接状态发生变化时触发。</summary>
    public event EventHandler<DebugConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>当发送、接收或产生可恢复错误时触发。</summary>
    public event EventHandler<DebugTrafficEntry>? TrafficOccurred;

    /// <summary>获取当前连接状态。</summary>
    public DebugConnectionState State
    {
        get
        {
            lock (_stateLock)
                return _state;
        }
    }

    /// <summary>
    /// 建立一个新的 TCP 连接并启动后台接收循环。
    /// </summary>
    /// <param name="host">远端主机名或 IP。</param>
    /// <param name="port">远端端口。</param>
    /// <param name="protocol">接收数据采用的协议。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task ConnectAsync(
        string host,
        int port,
        DeviceProtocol protocol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("服务器地址不能为空。", nameof(host));
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须是 1-65535。");
        if (State != DebugConnectionState.Disconnected)
            throw new InvalidOperationException("当前会话正在连接或已经连接。");

        SetState(DebugConnectionState.Connecting, $"正在连接 {host}:{port}…");
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            var receiveCancellation = new CancellationTokenSource();
            lock (_stateLock)
            {
                _client = client;
                _stream = client.GetStream();
                _receiveCancellation = receiveCancellation;
                _receiveTask = ReceiveLoopAsync(protocol, _stream, receiveCancellation.Token);
            }

            SetState(DebugConnectionState.Connected, $"已连接 {host}:{port}");
            Publish(TrafficDirection.System, "TCP 连接成功", string.Empty, $"{host}:{port} / {protocol}");
        }
        catch
        {
            client.Dispose();
            ClearConnectionReferences();
            SetState(DebugConnectionState.Disconnected, "连接失败");
            throw;
        }
    }

    /// <summary>
    /// 发送一条完整报文，并记录指令名称、原始 HEX 和生成摘要。
    /// </summary>
    /// <param name="bytes">待发送的完整字节序列。</param>
    /// <param name="title">指令显示名称。</param>
    /// <param name="details">报文生成摘要。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public async Task SendAsync(
        ReadOnlyMemory<byte> bytes,
        string title,
        string details,
        CancellationToken cancellationToken = default)
    {
        if (bytes.IsEmpty)
            throw new ArgumentException("发送报文不能为空。", nameof(bytes));

        NetworkStream stream;
        lock (_stateLock)
            stream = _stream ?? throw new InvalidOperationException("请先建立 TCP 连接。");

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            Publish(
                TrafficDirection.Send,
                title,
                HexCodec.ToHex(bytes.Span, true),
                details);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            Publish(TrafficDirection.Error, "发送失败", HexCodec.ToHex(bytes.Span, true), exception.Message);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 主动关闭当前 TCP 连接；重复调用不会产生异常。
    /// </summary>
    public async Task DisconnectAsync()
    {
        Task? receiveTask;
        CancellationTokenSource? cancellation;
        TcpClient? client;
        lock (_stateLock)
        {
            if (_state == DebugConnectionState.Disconnected)
                return;
            _state = DebugConnectionState.Disconnecting;
            receiveTask = _receiveTask;
            cancellation = _receiveCancellation;
            client = _client;
            _receiveTask = null;
            _receiveCancellation = null;
            _stream = null;
            _client = null;
        }

        StateChanged?.Invoke(
            this,
            new DebugConnectionStateChangedEventArgs(DebugConnectionState.Disconnecting, "正在断开连接…"));
        cancellation?.Cancel();
        client?.Dispose();
        if (receiveTask is not null)
        {
            try
            {
                await receiveTask;
            }
            catch (OperationCanceledException)
            {
                // 主动断开通过取消接收循环完成，不应在界面重复显示错误。
            }
        }

        cancellation?.Dispose();
        SetState(DebugConnectionState.Disconnected, "未连接");
        Publish(TrafficDirection.System, "TCP 连接已断开", string.Empty, "用户主动断开连接。");
    }

    /// <summary>
    /// 释放网络和同步资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _sendLock.Dispose();
    }

    private async Task ReceiveLoopAsync(
        DeviceProtocol protocol,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffered = new List<byte>();
        var chunk = new byte[ReceiveChunkSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(chunk, cancellationToken);
                if (count == 0)
                {
                    Publish(TrafficDirection.System, "远端关闭连接", string.Empty, "TCP 流返回 0 字节。");
                    break;
                }

                buffered.AddRange(chunk.AsSpan(0, count).ToArray());
                if (buffered.Count > MaximumBufferedBytes)
                {
                    Publish(
                        TrafficDirection.Error,
                        "接收缓冲区已重置",
                        HexCodec.ToHex(CollectionsMarshalHelper.TakeTail(buffered, 256), true),
                        $"连续 {buffered.Count} 字节未能形成完整帧，超过 1 MiB 安全上限。");
                    buffered.Clear();
                    continue;
                }

                DrainFrames(protocol, buffered);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 主动断开属于正常流程。
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            Publish(TrafficDirection.Error, "接收连接异常", string.Empty, exception.Message);
        }
        catch (Exception exception)
        {
            // 单条解析之外的未知错误也必须隔离，避免后台任务异常导致整个 UI 进程退出。
            Publish(TrafficDirection.Error, "接收循环异常", string.Empty, exception.ToString());
        }
        finally
        {
            CloseFromReceiveLoop();
        }
    }

    private void DrainFrames(DeviceProtocol protocol, List<byte> buffered)
    {
        while (buffered.Count > 0)
        {
            ProtocolFrameReadResult result;
            try
            {
                result = ProtocolFrameReader.Read(protocol, buffered.ToArray());
            }
            catch (Exception exception)
            {
                // 解析器不应抛出异常；若扩展解析器违反约定，只丢弃一个字节以继续恢复边界。
                var invalidByte = buffered[0];
                buffered.RemoveAt(0);
                Publish(
                    TrafficDirection.Error,
                    "协议解析器异常",
                    invalidByte.ToString("X2"),
                    exception.Message);
                continue;
            }

            if (result.NeedMoreData)
                return;
            if (result.Consumed <= 0)
            {
                buffered.RemoveAt(0);
                Publish(TrafficDirection.Error, "协议解析未消费数据", string.Empty, "已丢弃一个字节防止死循环。");
                continue;
            }

            buffered.RemoveRange(0, Math.Min(result.Consumed, buffered.Count));
            if (result.Frame is null)
                continue;

            Publish(
                result.Frame.IsValid ? TrafficDirection.Receive : TrafficDirection.Error,
                result.Frame.IsValid ? result.Frame.Summary : "收到非法协议帧",
                HexCodec.ToHex(result.Frame.Raw, true),
                FrameDisplayFormatter.Format(result.Frame));
        }
    }

    private void CloseFromReceiveLoop()
    {
        TcpClient? client;
        CancellationTokenSource? cancellation;
        lock (_stateLock)
        {
            client = _client;
            cancellation = _receiveCancellation;
            _client = null;
            _stream = null;
            _receiveTask = null;
            _receiveCancellation = null;
            _state = DebugConnectionState.Disconnected;
        }

        client?.Dispose();
        cancellation?.Dispose();
        StateChanged?.Invoke(
            this,
            new DebugConnectionStateChangedEventArgs(DebugConnectionState.Disconnected, "未连接"));
    }

    private void ClearConnectionReferences()
    {
        lock (_stateLock)
        {
            _client = null;
            _stream = null;
            _receiveCancellation = null;
            _receiveTask = null;
        }
    }

    private void SetState(DebugConnectionState state, string message)
    {
        lock (_stateLock)
            _state = state;
        StateChanged?.Invoke(this, new DebugConnectionStateChangedEventArgs(state, message));
    }

    private void Publish(
        TrafficDirection direction,
        string title,
        string rawHex,
        string details)
    {
        var entry = new DebugTrafficEntry(DateTimeOffset.Now, direction, title, rawHex, details);
        _fileLogger.TryWrite(entry);
        TrafficOccurred?.Invoke(this, entry);
    }
}
