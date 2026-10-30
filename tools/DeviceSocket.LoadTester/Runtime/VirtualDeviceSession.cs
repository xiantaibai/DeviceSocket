using System.Diagnostics;
using System.Net.Sockets;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Reporting;
using DeviceSocket.LoadTester.Workloads.Gather;

namespace DeviceSocket.LoadTester.Runtime;

/// <summary>
/// 管理单台虚拟终端从限速建连、协议登录、心跳和业务应答到断线退避重连的完整生命周期，并把结果写入共享指标。
/// </summary>
/// <param name="descriptor">本设备稳定的协议身份、全局序号和随机种子。</param>
/// <param name="options">连接、心跳、上报、重连和协议兼容配置。</param>
/// <param name="protocol">负责创建帧、拆包和确认关联的协议适配器。</param>
/// <param name="metrics">本节点共享的并发安全指标容器。</param>
/// <param name="onlineRegistry">供 HTTP 指令工作负载按设备序号筛选在线目标的注册表。</param>
/// <param name="pacer">所有设备共享的连接速率调度器。</param>
/// <param name="connectionGate">限制同时处于连接和登录过程中的设备数量。</param>
/// <param name="faultController">提供当前场景阶段的故障参数快照。</param>
/// <param name="commandTraceRegistry">可选 HTTP 期望命令关联器；未启用下行诊断时为空。</param>
internal sealed class VirtualDeviceSession(
    VirtualDeviceDescriptor descriptor,
    LoadTestOptions options,
    ILoadTestProtocol protocol,
    LoadTestMetrics metrics,
    IDeviceOnlineRegistry onlineRegistry,
    ConnectionPacer pacer,
    SemaphoreSlim connectionGate,
    FaultInjectionController faultController,
    GatherCommandTraceRegistry? commandTraceRegistry = null)
{
    private readonly Random _random = new(descriptor.RandomSeed);
    private readonly object _activeCycleLock = new();
    private CancellationTokenSource? _activeCycleCancellation;
    private int _forcedDisconnectRequested;
    private byte _sequence;
    private PriorityQueue<PendingDownstreamResponse, long>? _pendingResponses;
    private GatherV4SessionCommandObserver? _gatherV4Observer;
    private long _sessionGeneration;

    internal VirtualDeviceState State { get; private set; } = VirtualDeviceState.Created;

    internal void RequestDisconnect()
    {
        Interlocked.Exchange(ref _forcedDisconnectRequested, 1);
        lock (_activeCycleLock)
            _activeCycleCancellation?.Cancel();
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        var reconnectStrategy = new ReconnectDelayStrategy(options.Reconnect, _random);
        var retryAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var reachedOnline = await RunConnectionCycleAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                break;
            if (reachedOnline)
                retryAttempt = 0;

            if (options.Reconnect.MaximumAttempts > 0 && retryAttempt >= options.Reconnect.MaximumAttempts)
            {
                metrics.PermanentlyStopped();
                break;
            }

            retryAttempt++;
            metrics.ReconnectScheduled();
            State = VirtualDeviceState.Backoff;
            metrics.SetBackoff(true);
            try
            {
                var delay = reconnectStrategy.GetDelay(retryAttempt);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                metrics.SetBackoff(false);
            }
        }

        State = VirtualDeviceState.Stopped;
    }

    /// <summary>执行一次连接、登录及在线周期，结束时取消延迟回复并清理当前会话观察状态。</summary>
    /// <param name="cancellationToken">整轮设备任务取消令牌。</param>
    /// <returns>本周期曾成功上线时为 true，供重连策略重置退避。</returns>
    private async Task<bool> RunConnectionCycleAsync(CancellationToken cancellationToken)
    {
        TcpClient? client = null;
        NetworkStream? stream = null;
        var gateHeld = false;
        var tcpConnected = false;
        var online = false;
        var reachedOnline = false;
        var loginStage = false;
        var connectionAttemptActive = false;
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeCycleLock)
        {
            _activeCycleCancellation = cycleCancellation;
            if (Volatile.Read(ref _forcedDisconnectRequested) != 0)
                cycleCancellation.Cancel();
        }
        try
        {
            State = VirtualDeviceState.WaitingToConnect;
            await pacer.WaitAsync(cycleCancellation.Token);
            await connectionGate.WaitAsync(cycleCancellation.Token);
            gateHeld = true;
            State = VirtualDeviceState.Connecting;
            metrics.ConnectionStarted();
            connectionAttemptActive = true;

            client = new TcpClient { NoDelay = options.Connection.NoDelay };
            var stopwatch = Stopwatch.StartNew();
            await ConnectAsync(client, cycleCancellation.Token);
            metrics.ConnectionOpened(stopwatch.Elapsed);
            connectionAttemptActive = false;
            tcpConnected = true;
            _sessionGeneration++;
            stream = client.GetStream();
            commandTraceRegistry?.ResetDeviceSession(descriptor.DeviceId);
            using var reader = new ProtocolSocketReader(protocol);

            State = VirtualDeviceState.LoggingIn;
            loginStage = true;
            metrics.LoginStarted();
            stopwatch.Restart();
            await LoginAsync(stream, reader, cycleCancellation.Token);
            metrics.LoginCompleted(stopwatch.Elapsed, DateTimeOffset.UtcNow);
            onlineRegistry.RegisterOnline(descriptor.GlobalOrdinal);
            online = true;
            reachedOnline = true;
            loginStage = false;
            State = VirtualDeviceState.Online;

            connectionGate.Release();
            gateHeld = false;
            await RunOnlineAsync(stream, reader, cycleCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 场景结束或阶段缩容属于正常停止，不写入错误指标。
        }
        catch (OperationCanceledException) when (Interlocked.Exchange(ref _forcedDisconnectRequested, 0) != 0)
        {
            metrics.ForcedDisconnect();
        }
        catch (DeviceTimeoutException exception)
        {
            RecordTimeout(exception, tcpConnected, loginStage);
            if (exception.Kind == DeviceTimeoutKind.Connect)
                connectionAttemptActive = false;
        }
        catch (RemoteClosedException)
        {
            metrics.RemoteClosed();
            if (loginStage)
                metrics.LoginFailed(false, "LOGIN_REMOTE_CLOSED");
        }
        catch (SocketException exception)
        {
            if (!tcpConnected)
            {
                metrics.ConnectionFailed(false, $"SOCKET_{exception.SocketErrorCode}");
                connectionAttemptActive = false;
            }
            else
            {
                metrics.RecordError($"SOCKET_{exception.SocketErrorCode}");
                if (loginStage)
                    metrics.LoginFailed(false, "LOGIN_SOCKET_ERROR");
            }
        }
        catch (IOException)
        {
            if (!tcpConnected)
            {
                metrics.ConnectionFailed(false, "CONNECT_IO_ERROR");
                connectionAttemptActive = false;
            }
            else
            {
                metrics.RecordError("CONNECTION_IO_ERROR");
                if (loginStage)
                    metrics.LoginFailed(false, "LOGIN_IO_ERROR");
            }
        }
        catch (Exception)
        {
            if (!tcpConnected)
            {
                metrics.ConnectionFailed(false, "CONNECT_UNEXPECTED_ERROR");
                connectionAttemptActive = false;
            }
            else
            {
                metrics.RecordError("SESSION_UNEXPECTED_ERROR");
                if (loginStage)
                    metrics.LoginFailed(false, "LOGIN_UNEXPECTED_ERROR");
            }
        }
        finally
        {
            cycleCancellation.Cancel();
            // 延迟回复属于当前 Socket 会话，断线后必须全部取消，不能写入后续重连的新 Session。
            if (_pendingResponses is not null)
            {
                while (_pendingResponses.TryDequeue(out var pending, out _))
                    if (pending.IsGatherV4)
                    {
                        metrics.GatherV4Commands.Dequeued(cancelled: true);
                        RecordGatherV4Sample(pending, "cancelled");
                    }
                _pendingResponses = null;
            }
            _gatherV4Observer = null;
            lock (_activeCycleLock)
            {
                if (ReferenceEquals(_activeCycleCancellation, cycleCancellation))
                    _activeCycleCancellation = null;
            }
            if (Interlocked.Exchange(ref _forcedDisconnectRequested, 0) != 0)
                metrics.ForcedDisconnect();
            if (online)
                onlineRegistry.UnregisterOnline(descriptor.GlobalOrdinal);
            if (cancellationToken.IsCancellationRequested && online && stream is not null)
                await TrySendLogoutAsync(stream);
            client?.Dispose();
            if (gateHeld)
                connectionGate.Release();
            if (connectionAttemptActive)
                metrics.ConnectionCancelled();
            if (tcpConnected)
                metrics.ConnectionClosed(online, DateTimeOffset.UtcNow);
        }

        return reachedOnline;
    }

    private async Task ConnectAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Connection.ConnectTimeoutSeconds));
        try
        {
            await client.ConnectAsync(options.Target.Host, options.Target.Port, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceTimeoutException(DeviceTimeoutKind.Connect, "TCP 建连超时。");
        }
    }

    /// <summary>发送登录并验证 ACK；同一批 TCP 数据中 ACK 后的命令仍需处理，避免粘包时静默丢弃首条任务。</summary>
    /// <param name="stream">当前连接网络流。</param>
    /// <param name="reader">当前连接流式拆包器。</param>
    /// <param name="cancellationToken">连接生命周期取消令牌。</param>
    /// <returns>合法登录确认完成的异步任务。</returns>
    /// <exception cref="DeviceTimeoutException">超过登录等待上限时抛出。</exception>
    private async Task LoginAsync(
        NetworkStream stream,
        ProtocolSocketReader reader,
        CancellationToken cancellationToken)
    {
        var sequence = NextSequence();
        var frame = protocol.CreateLoginFrame(descriptor, sequence);
        var bytesWritten = await WriteAsync(stream, frame, cancellationToken);
        metrics.BytesSent(bytesWritten);
        if (!protocol.RequiresLoginConfirmation)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Connection.LoginTimeoutSeconds));
        try
        {
            while (true)
            {
                var bytesRead = await reader.StartReadAsync(stream, timeout.Token);
                if (bytesRead == 0)
                    throw new RemoteClosedException();
                metrics.BytesReceived(bytesRead);
                var loginConfirmed = false;
                foreach (var response in reader.CompleteRead(bytesRead))
                {
                    metrics.FrameReceived(response.IsValid);
                    if (DropInboundFrame())
                        continue;
                    if (protocol.IsConfirmation(response, descriptor, ProtocolOperation.Login, sequence))
                        loginConfirmed = true;
                    else
                        await RespondToDownstreamAsync(stream, response, timeout.Token);
                }
                if (loginConfirmed)
                    return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceTimeoutException(DeviceTimeoutKind.Login, "等待协议登录确认超时。");
        }
    }

    /// <summary>在同一会话循环调度心跳、主动上报、接收和有界延迟应答，保持网络流写入串行。</summary>
    /// <param name="stream">当前 Socket 的网络流。</param>
    /// <param name="reader">当前连接拆包器。</param>
    /// <param name="cancellationToken">当前连接生命周期令牌。</param>
    /// <returns>断线或取消时结束的异步任务。</returns>
    private async Task RunOnlineAsync(
        NetworkStream stream,
        ProtocolSocketReader reader,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var nextHeartbeat = options.Heartbeat.Enabled ? NextHeartbeatAt(now) : DateTimeOffset.MaxValue;
        var nextReport = options.Workload.ReportsEnabled ? NextReportAt(now) : DateTimeOffset.MaxValue;
        DateTimeOffset? heartbeatDeadline = null;
        DateTimeOffset heartbeatSentAt = default;
        byte heartbeatSequence = 0;
        Task<int>? pendingRead = null;
        using var wakeup = new SessionWakeupTimer(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            now = DateTimeOffset.UtcNow;
            if (heartbeatDeadline is { } deadline && deadline <= now)
            {
                metrics.HeartbeatTimedOut();
                throw new DeviceTimeoutException(DeviceTimeoutKind.Heartbeat, "等待心跳确认超时。");
            }

            if (nextHeartbeat <= now && heartbeatDeadline is null)
            {
                heartbeatSequence = NextSequence();
                var heartbeat = protocol.CreateHeartbeatFrame(descriptor, heartbeatSequence, now);
                var bytesWritten = await WriteAsync(stream, heartbeat, cancellationToken);
                metrics.HeartbeatSent(bytesWritten);
                heartbeatSentAt = now;
                if (protocol.RequiresHeartbeatConfirmation)
                    heartbeatDeadline = now.AddSeconds(options.Heartbeat.ResponseTimeoutSeconds);
                nextHeartbeat = NextHeartbeatAt(now);
            }

            if (nextReport <= now)
            {
                var report = protocol.CreateReportFrame(descriptor, NextSequence(), now);
                if (report is not null)
                {
                    var bytesWritten = await WriteAsync(stream, report, cancellationToken);
                    metrics.ReportSent(bytesWritten);
                }
                nextReport = NextReportAt(now);
            }

            // 复用已有会话定时调度，最多连续发送 16 条到期回复后重新处理心跳及接收；不为每条应答创建 Timer 或阻塞任务。
            for (var sent = 0; sent < 16 && _pendingResponses is not null &&
                 _pendingResponses.TryPeek(out _, out var responseDue) && responseDue <= DateTimeOffset.UtcNow.UtcTicks; sent++)
            {
                var pending = _pendingResponses.Dequeue();
                if (pending.IsGatherV4) metrics.GatherV4Commands.Dequeued(cancelled: false);
                await SendDownstreamResponseAsync(stream, pending, cancellationToken);
            }

            pendingRead ??= reader.StartReadAsync(stream, cancellationToken);
            var nextDue = Min(nextHeartbeat, nextReport, heartbeatDeadline ?? DateTimeOffset.MaxValue);
            if (_pendingResponses is not null && _pendingResponses.TryPeek(out _, out var queuedDue))
                nextDue = nextDue < new DateTimeOffset(queuedDue, TimeSpan.Zero) ? nextDue : new DateTimeOffset(queuedDue, TimeSpan.Zero);
            var pendingTimer = wakeup.WaitUntil(nextDue);
            var completed = await Task.WhenAny(pendingRead, pendingTimer);
            if (completed != pendingRead)
            {
                await pendingTimer;
                continue;
            }

            var bytesRead = await pendingRead;
            pendingRead = null;
            if (bytesRead == 0)
                throw new RemoteClosedException();
            metrics.BytesReceived(bytesRead);
            foreach (var response in reader.CompleteRead(bytesRead))
            {
                metrics.FrameReceived(response.IsValid);
                if (DropInboundFrame())
                    continue;
                if (heartbeatDeadline is not null &&
                    protocol.IsConfirmation(response, descriptor, ProtocolOperation.Heartbeat, heartbeatSequence))
                {
                    metrics.HeartbeatConfirmed(DateTimeOffset.UtcNow - heartbeatSentAt);
                    heartbeatDeadline = null;
                }
                else
                {
                    await RespondToDownstreamAsync(stream, response, cancellationToken);
                }
            }
        }
    }

    /// <summary>
    /// 执行协议适配器生成的下行响应计划，并把旧 Gather 读表或 GatherV4 命令、丢弃、延迟和重复应答单独计量。
    /// </summary>
    /// <param name="stream">当前设备连接的网络流。</param>
    /// <param name="response">服务端下发且已完成协议解析的帧。</param>
    /// <param name="cancellationToken">连接周期取消令牌。</param>
    /// <returns>响应计划执行完成的异步任务。</returns>
    private async Task RespondToDownstreamAsync(
        NetworkStream stream,
        IProtocolFrame response,
        CancellationToken cancellationToken)
    {
        metrics.DownstreamFrame();
        var classification = protocol is IDownstreamFrameClassifier classifier
            ? classifier.ClassifyDownstream(response, descriptor)
            : null;
        var traceMatch = classification is null
            ? null
            : commandTraceRegistry?.RecordDownstream(descriptor, classification, DateTimeOffset.UtcNow);
        var plan = protocol.CreateDownstreamResponse(response, descriptor, NextSequence());
        if (plan is null)
            return;
        var isGatherReading = plan.Kind == DownstreamResponseKind.GatherReading;
        var isGatherV4 = plan.Kind == DownstreamResponseKind.GatherV4Command;
        var receivedAt = DateTimeOffset.UtcNow;
        if (isGatherV4)
        {
            _gatherV4Observer ??= new GatherV4SessionCommandObserver(metrics.GatherV4Commands, options.Workload.GatherV4Response.RepeatObservationSeconds);
            _gatherV4Observer.Received(classification!.Fingerprint, receivedAt);
        }
        if (isGatherReading)
            metrics.GatherReadingCommandReceived();
        if (plan.Payload is null || plan.SendCount <= 0)
        {
            if (isGatherReading)
                metrics.GatherReadingResponseDropped();
            if (isGatherV4)
            {
                metrics.GatherV4Commands.Dropped();
                RecordGatherV4Sample(new PendingDownstreamResponse(plan, traceMatch, classification?.Fingerprint, receivedAt, true, response.Summary), "intentionalDrop");
            }
            return;
        }

        var pending = new PendingDownstreamResponse(plan, traceMatch, classification?.Fingerprint, receivedAt, isGatherV4, response.Summary);
        if (plan.Delay > TimeSpan.Zero)
        {
            _pendingResponses ??= new();
            if (_pendingResponses.Count >= options.Workload.GatherV4Response.MaximumPendingResponsesPerDevice)
            {
                if (isGatherV4) metrics.GatherV4Commands.Rejected();
                if (isGatherV4) RecordGatherV4Sample(pending, "queueRejected");
                metrics.RecordError("RESPONSE_QUEUE_FULL");
                return;
            }
            _pendingResponses.Enqueue(pending, receivedAt.Add(plan.Delay).UtcTicks);
            if (isGatherV4) metrics.GatherV4Commands.Queued();
            return;
        }
        await SendDownstreamResponseAsync(stream, pending, cancellationToken);
    }

    /// <summary>串行写出即时或到期的回复，区分正确确认、故障注入与写出失败；不等待业务延迟。</summary>
    /// <param name="stream">所属连接网络流。</param>
    /// <param name="pending">已识别的回复及接收上下文。</param>
    /// <param name="cancellationToken">所属连接取消令牌。</param>
    /// <returns>回复写入完成的异步任务，网络错误交由连接生命周期恢复。</returns>
    private async Task SendDownstreamResponseAsync(NetworkStream stream, PendingDownstreamResponse pending, CancellationToken cancellationToken)
    {
        var plan = pending.Plan;
        var isGatherReading = plan.Kind == DownstreamResponseKind.GatherReading;
        var isTrackedCommand = plan.Kind is DownstreamResponseKind.GatherReading or DownstreamResponseKind.GatherV4Command;

        var responded = false;
        try
        {
            for (var index = 0; index < plan.SendCount; index++)
            {
                var bytesWritten = await WriteAsync(stream, plan.Payload!, cancellationToken);
                if (bytesWritten <= 0)
                {
                    if (pending.IsGatherV4) { metrics.GatherV4Commands.WriteFailed(); RecordGatherV4Sample(pending, "writeFailed"); }
                    continue;
                }
                metrics.DownstreamResponseSent(bytesWritten);
                if (isGatherReading)
                    metrics.GatherReadingResponseSent(plan.IsBusinessFailure, duplicate: index > 0);

                if (pending.IsGatherV4)
                {
                    if (plan.ResponseMatchesRequest && index == 0)
                    {
                        _gatherV4Observer!.Confirmed(pending.Fingerprint!, pending.ReceivedAt, DateTimeOffset.UtcNow);
                        RecordGatherV4Sample(pending, "f1Sent", plan.Payload);
                    }
                    else
                    {
                        metrics.GatherV4Commands.FaultSent();
                        RecordGatherV4Sample(pending, "faultSent", plan.Payload);
                    }
                }
                if (isTrackedCommand && !responded && plan.ResponseMatchesRequest)
                {
                    if (isGatherReading)
                        metrics.GatherReadingCommandResponded(DateTimeOffset.UtcNow - pending.ReceivedAt);
                    commandTraceRegistry?.RecordResponse(pending.TraceMatch?.RequestId, DateTimeOffset.UtcNow, pending.TraceMatch?.Fingerprint);
                    responded = true;
                }
            }
        }
        catch
        {
            if (pending.IsGatherV4) { metrics.GatherV4Commands.WriteFailed(); RecordGatherV4Sample(pending, "writeFailed"); }
            throw;
        }

        if (isGatherReading && !responded)
            metrics.GatherReadingResponseDropped();
    }

    /// <summary>保存当前连接内一条待发送回复，不持有其他会话或新连接的流引用。</summary>
    /// <param name="Plan">协议回复计划。</param>
    /// <param name="TraceMatch">可空 HTTP 子命令关联。</param>
    /// <param name="Fingerprint">下行完整帧指纹。</param>
    /// <param name="ReceivedAt">本次命令收到时间。</param>
    /// <param name="IsGatherV4">是否计入 GatherV4 独立统计。</param>
    /// <param name="RequestInstruction">原始下行指令摘要。</param>
    private sealed record PendingDownstreamResponse(DownstreamResponsePlan Plan, DownstreamTraceMatch? TraceMatch, string? Fingerprint, DateTimeOffset ReceivedAt, bool IsGatherV4, string RequestInstruction);

    /// <summary>把当前应答事实写入节点有界样本队列，不写同步逐帧日志。</summary>
    /// <param name="pending">当前会话回复上下文。</param>
    /// <param name="outcome">固定结果分类。</param>
    /// <param name="response">已成功写出的字节；没有写出时为空。</param>
    private void RecordGatherV4Sample(PendingDownstreamResponse pending, string outcome, byte[]? response = null) =>
        metrics.GatherV4Commands.RecordSample(new GatherV4CommandSample
        {
            DeviceId = descriptor.DeviceId,
            SessionGeneration = _sessionGeneration,
            ReceivedAtUtc = pending.ReceivedAt,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Fingerprint = pending.Fingerprint,
            Outcome = outcome,
            RequestInstruction = pending.RequestInstruction,
            ResponseHex = response is null ? null : Convert.ToHexString(response)
        });

    private async Task<int> WriteAsync(NetworkStream stream, byte[] frame, CancellationToken cancellationToken)
    {
        var faults = faultController.Current;
        if (faults.NetworkLatencyMilliseconds > 0)
            await Task.Delay(faults.NetworkLatencyMilliseconds, cancellationToken);
        if (FaultInjectionController.ShouldInject(faults.OutboundPacketLossRate, _random))
        {
            metrics.InjectedOutboundDrop();
            return 0;
        }

        var payload = frame;
        if (frame.Length >= 2 && FaultInjectionController.ShouldInject(faults.CorruptChecksumRate, _random))
        {
            payload = frame.ToArray();
            payload[^2] ^= 0x01;
            metrics.InjectedCorruptFrame();
        }

        if (FaultInjectionController.ShouldInject(faults.DuplicateFrameRate, _random))
        {
            var duplicate = new byte[payload.Length * 2];
            payload.CopyTo(duplicate, 0);
            payload.CopyTo(duplicate, payload.Length);
            payload = duplicate;
            metrics.InjectedDuplicateFrame();
        }

        if (faults.FragmentSizeBytes > 0 && faults.FragmentSizeBytes < payload.Length)
        {
            metrics.InjectedFragmentedWrite();
            var written = 0;
            while (written < payload.Length)
            {
                var count = Math.Min(faults.FragmentSizeBytes, payload.Length - written);
                await WriteRawAsync(stream, payload.AsMemory(written, count), cancellationToken);
                written += count;
                if (written < payload.Length && faults.FragmentDelayMilliseconds > 0)
                    await Task.Delay(faults.FragmentDelayMilliseconds, cancellationToken);
            }

            return written;
        }

        await WriteRawAsync(stream, payload, cancellationToken);
        return payload.Length;
    }

    private async Task WriteRawAsync(
        NetworkStream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Connection.WriteTimeoutSeconds));
        try
        {
            await stream.WriteAsync(payload, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceTimeoutException(DeviceTimeoutKind.Write, "Socket 写入超时。");
        }
    }

    private async Task TrySendLogoutAsync(NetworkStream stream)
    {
        var logout = protocol.CreateLogoutFrame(descriptor, NextSequence());
        if (logout is null)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            var bytesWritten = await WriteAsync(stream, logout, timeout.Token);
            metrics.BytesSent(bytesWritten);
        }
        catch
        {
            // 场景停止时退出登录属于尽力而为，失败不能掩盖主要压测结果。
        }
    }

    private void RecordTimeout(DeviceTimeoutException exception, bool tcpConnected, bool loginStage)
    {
        switch (exception.Kind)
        {
            case DeviceTimeoutKind.Connect:
                metrics.ConnectionFailed(true, "CONNECT_TIMEOUT");
                break;
            case DeviceTimeoutKind.Login:
                metrics.LoginFailed(true, "LOGIN_TIMEOUT");
                break;
            case DeviceTimeoutKind.Write:
                metrics.WriteTimedOut();
                if (loginStage)
                    metrics.LoginFailed(false, "LOGIN_WRITE_TIMEOUT");
                break;
            case DeviceTimeoutKind.Heartbeat:
                break;
            default:
                metrics.RecordError("UNKNOWN_TIMEOUT");
                break;
        }
    }

    private DateTimeOffset NextHeartbeatAt(DateTimeOffset now) =>
        now.AddSeconds(JitteredInterval(options.Heartbeat.IntervalSeconds, options.Heartbeat.JitterSeconds));

    private DateTimeOffset NextReportAt(DateTimeOffset now) =>
        now.AddSeconds(JitteredInterval(options.Workload.ReportIntervalSeconds, options.Workload.JitterSeconds));

    private int JitteredInterval(int interval, int jitter) =>
        jitter <= 0 ? interval : Math.Max(1, interval + _random.Next(-jitter, jitter + 1));

    private byte NextSequence()
    {
        _sequence = (byte)((_sequence + 1) & 0x0F);
        return _sequence;
    }

    private bool DropInboundFrame()
    {
        if (!FaultInjectionController.ShouldInject(faultController.Current.InboundPacketLossRate, _random))
            return false;
        metrics.InjectedInboundDrop();
        return true;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second, DateTimeOffset third) =>
        first <= second && first <= third ? first : second <= third ? second : third;
}
