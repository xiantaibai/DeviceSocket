using System.Net.Sockets;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
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
    private DeviceProtocol? _protocol;
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
                _protocol = protocol;
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
    /// <param name="trackResponse">是否把该发送记录登记为等待服务端回复的请求；自动终端应答必须设为 false。</param>
    public async Task SendAsync(
        ReadOnlyMemory<byte> bytes,
        string title,
        string details,
        CancellationToken cancellationToken = default,
        bool trackResponse = true)
    {
        if (bytes.IsEmpty)
            throw new ArgumentException("发送报文不能为空。", nameof(bytes));

        NetworkStream stream;
        DeviceProtocol protocol;
        lock (_stateLock)
        {
            stream = _stream ?? throw new InvalidOperationException("请先建立 TCP 连接。");
            protocol = _protocol ?? throw new InvalidOperationException("当前连接缺少协议状态。");
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            var frame = TryReadCompleteFrame(protocol, bytes.Span);
            Publish(
                TrafficDirection.Send,
                title,
                HexCodec.ToHex(bytes.Span, true),
                details,
                frame,
                trackResponse);
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
            _protocol = null;
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

    /// <summary>
    /// 持续读取 TCP 字节流并交给协议拆包器，直到连接关闭或收到取消请求。
    /// </summary>
    /// <param name="protocol">当前连接使用的协议。</param>
    /// <param name="stream">已经建立连接的网络流。</param>
    /// <param name="cancellationToken">主动断开时使用的取消标记。</param>
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

    /// <summary>
    /// 从连续缓冲区中尽可能解析完整协议帧，并为每帧发布结构化通信记录。
    /// </summary>
    /// <param name="protocol">当前连接使用的协议。</param>
    /// <param name="buffered">尚未被协议解析器消费的字节。</param>
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
                FrameDisplayFormatter.Format(result.Frame),
                result.Frame);
        }
    }

    /// <summary>
    /// 在接收循环结束后原子清理连接引用，并通知界面恢复未连接状态。
    /// </summary>
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
            _protocol = null;
            _state = DebugConnectionState.Disconnected;
        }

        client?.Dispose();
        cancellation?.Dispose();
        StateChanged?.Invoke(
            this,
            new DebugConnectionStateChangedEventArgs(DebugConnectionState.Disconnected, "未连接"));
    }

    /// <summary>
    /// 在连接建立失败时清理尚未进入正常生命周期的连接和协议引用。
    /// </summary>
    private void ClearConnectionReferences()
    {
        lock (_stateLock)
        {
            _client = null;
            _stream = null;
            _receiveCancellation = null;
            _receiveTask = null;
            _protocol = null;
        }
    }

    /// <summary>
    /// 在线程锁内更新连接状态并向订阅者发布状态说明。
    /// </summary>
    /// <param name="state">新的连接状态。</param>
    /// <param name="message">适合界面展示的状态说明。</param>
    private void SetState(DebugConnectionState state, string message)
    {
        lock (_stateLock)
            _state = state;
        StateChanged?.Invoke(this, new DebugConnectionStateChangedEventArgs(state, message));
    }

    /// <summary>
    /// 创建通信记录、尽力写入文件日志并通知界面订阅者。
    /// </summary>
    /// <param name="direction">通信方向。</param>
    /// <param name="title">记录标题。</param>
    /// <param name="rawHex">完整原始报文 HEX。</param>
    /// <param name="details">解析详情或状态说明。</param>
    /// <param name="frame">可选的结构化协议帧，用于生成分类和筛选元数据。</param>
    /// <param name="trackResponse">发送记录是否应进入请求回复关联队列。</param>
    private void Publish(
        TrafficDirection direction,
        string title,
        string rawHex,
        string details,
        IProtocolFrame? frame = null,
        bool trackResponse = true)
    {
        var category = ResolveCategory(direction, frame);
        var metadata = frame is null ? null : CreateMetadata(frame);
        var entry = new DebugTrafficEntry(
            DateTimeOffset.Now,
            direction,
            title,
            rawHex,
            details,
            category,
            metadata,
            trackResponse);
        _fileLogger.TryWrite(entry);
        TrafficOccurred?.Invoke(this, entry);
    }

    /// <summary>
    /// 尝试把已经发送的完整字节序列解析为协议帧；元数据解析失败不影响真实报文发送。
    /// </summary>
    /// <param name="protocol">当前连接协议。</param>
    /// <param name="bytes">已经写入网络流的完整报文。</param>
    /// <returns>完整协议帧；无法完整解析时返回 null。</returns>
    private static IProtocolFrame? TryReadCompleteFrame(DeviceProtocol protocol, ReadOnlySpan<byte> bytes)
    {
        try
        {
            var result = ProtocolFrameReader.Read(protocol, bytes);
            return !result.NeedMoreData && result.Consumed == bytes.Length
                ? result.Frame
                : null;
        }
        catch (Exception)
        {
            // 发送已经成功，附加的分类元数据不能反向破坏发送链路。
            return null;
        }
    }

    /// <summary>
    /// 根据方向和协议业务类型决定界面分类。
    /// </summary>
    /// <param name="direction">通信方向。</param>
    /// <param name="frame">可选的结构化协议帧。</param>
    /// <returns>业务、心跳、异常或系统分类。</returns>
    private static DebugTrafficCategory ResolveCategory(TrafficDirection direction, IProtocolFrame? frame)
    {
        if (direction == TrafficDirection.Error || frame is { IsValid: false })
            return DebugTrafficCategory.Error;
        if (direction == TrafficDirection.System)
            return DebugTrafficCategory.System;

        return frame?.TrafficKind switch
        {
            ProtocolTrafficKind.Heartbeat or ProtocolTrafficKind.HeartbeatConfirmation =>
                DebugTrafficCategory.Heartbeat,
            ProtocolTrafficKind.LinkManagement or ProtocolTrafficKind.LinkConfirmation =>
                DebugTrafficCategory.System,
            _ => DebugTrafficCategory.Business
        };
    }

    /// <summary>
    /// 从具体协议帧提取设备、指令和序号字段，避免界面通过标题文本猜测报文类型。
    /// </summary>
    /// <param name="frame">已经完成协议解析的帧。</param>
    /// <returns>适合筛选和关联的结构化字段。</returns>
    private static DebugTrafficMetadata CreateMetadata(IProtocolFrame frame)
    {
        return frame switch
        {
            QGdw3761Frame qGdw => new DebugTrafficMetadata(
                qGdw.Protocol,
                qGdw.TrafficKind,
                qGdw.DeviceId ?? string.Empty,
                CreateQGdwInstructionKey(qGdw),
                qGdw.Afn,
                qGdw.Functions,
                qGdw.Sequence,
                qGdw.IsUpstream,
                qGdw.IsPrimaryMessage,
                qGdw.ConfirmationRequested),
            Dlt645Frame dlt645 => new DebugTrafficMetadata(
                dlt645.Protocol,
                dlt645.TrafficKind,
                dlt645.DeviceId ?? string.Empty,
                CreateDlt645InstructionKey(dlt645),
                null,
                Array.Empty<int>(),
                null),
            _ => new DebugTrafficMetadata(
                frame.Protocol,
                frame.TrafficKind,
                frame.DeviceId ?? string.Empty,
                frame.Protocol.ToString(),
                null,
                Array.Empty<int>(),
                null)
        };
    }

    /// <summary>
    /// 创建 376.1 AFN/FN 筛选键。
    /// </summary>
    /// <param name="frame">376.1 协议帧。</param>
    /// <returns>例如“AFN=0CH / F129”的显示文本。</returns>
    private static string CreateQGdwInstructionKey(QGdw3761Frame frame)
    {
        var functions = frame.Functions.Count == 0
            ? "无功能项"
            : string.Join(",", frame.Functions.Select(value => $"F{value}"));
        return $"AFN={frame.Afn:X2}H / {functions}";
    }

    /// <summary>
    /// 创建 645 功能码和数据标识筛选键。
    /// </summary>
    /// <param name="frame">DL/T 645-2007 协议帧。</param>
    /// <returns>功能码以及可选数据标识的显示文本。</returns>
    private static string CreateDlt645InstructionKey(Dlt645Frame frame)
    {
        var dataIdentifier = frame.DataIdentifier.HasValue
            ? $" / DI={frame.DataIdentifier.Value:X8}"
            : string.Empty;
        return $"C={frame.FunctionCode:X2}H{dataIdentifier}";
    }
}
