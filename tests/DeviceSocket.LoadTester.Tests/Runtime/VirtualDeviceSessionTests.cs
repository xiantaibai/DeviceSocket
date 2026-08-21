using System.Net;
using System.Net.Sockets;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Tests.Runtime;

/// <summary>
/// 验证真实回环 TCP 中的376.1登录确认、远端断开检测和自动重连闭环。
/// </summary>
public sealed class VirtualDeviceSessionTests
{
    /// <summary>
    /// 验证 Gather 兼容会话可通过真实 TCP 完成老式 F1 登录确认和心跳确认，不会误触发超时重连。
    /// </summary>
    [Fact]
    public async Task GatherLegacyAcknowledgement_CompletesLoginAndHeartbeat()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Target.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherTcpCenter;
        options.Heartbeat.IntervalSeconds = 1;
        options.Heartbeat.JitterSeconds = 0;
        options.Heartbeat.ResponseTimeoutSeconds = 2;
        var descriptor = Assert.Single(DeviceAddressAllocator.Allocate(options));
        var protocol = LoadTestProtocolFactory.Create(options.Target);
        var metrics = new LoadTestMetrics();
        var onlineRegistry = new DeviceOnlineRegistry();
        metrics.SetExpectedOnline(1, DateTimeOffset.UtcNow);
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var faults = new FaultInjectionController();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var serverTask = ServeGatherLoginAndHeartbeatAsync(listener, cancellation.Token);
        var session = new VirtualDeviceSession(
            descriptor,
            options,
            protocol,
            metrics,
            onlineRegistry,
            pacer,
            gate,
            faults);
        var sessionTask = session.RunAsync(cancellation.Token);

        await WaitUntilAsync(
            () => onlineRegistry.IsOnline(descriptor.GlobalOrdinal),
            TimeSpan.FromSeconds(3));
        Assert.Equal(1, onlineRegistry.Count);
        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).HeartbeatsConfirmed >= 1,
            TimeSpan.FromSeconds(5));
        await serverTask;
        cancellation.Cancel();
        await sessionTask;
        listener.Stop();

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.LoginSucceeded);
        Assert.True(snapshot.HeartbeatsConfirmed >= 1);
        Assert.Equal(0, snapshot.LoginTimeouts);
        Assert.Equal(0, snapshot.HeartbeatTimeouts);
        Assert.False(onlineRegistry.IsOnline(descriptor.GlobalOrdinal));
        Assert.Equal(0, onlineRegistry.Count);
    }

    /// <summary>
    /// 验证 TCP 已建立但服务端不返回登录确认时，设备会按登录超时关闭旧连接并自动重连。
    /// </summary>
    [Fact]
    public async Task LoginTimeout_ClosesConnectionAndReconnects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Target.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        options.Connection.LoginTimeoutSeconds = 1;
        options.Reconnect.MaximumAttempts = 3;
        var descriptor = Assert.Single(DeviceAddressAllocator.Allocate(options));
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var metrics = new LoadTestMetrics();
        metrics.SetExpectedOnline(1, DateTimeOffset.UtcNow);
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var faults = new FaultInjectionController();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var serverTask = ServeTimeoutThenLoginAsync(listener, cancellation.Token);
        var session = new VirtualDeviceSession(
            descriptor,
            options,
            protocol,
            metrics,
            new DeviceOnlineRegistry(),
            pacer,
            gate,
            faults);
        var sessionTask = session.RunAsync(cancellation.Token);

        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).LoginSucceeded >= 1,
            TimeSpan.FromSeconds(5));
        await serverTask;
        cancellation.Cancel();
        await sessionTask;
        listener.Stop();

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.LoginTimeouts);
        Assert.True(snapshot.ConnectionSucceeded >= 2);
        Assert.True(snapshot.ReconnectsScheduled >= 1);
    }

    /// <summary>
    /// 验证 645 虚拟设备完成地址绑定后，能识别服务端读数据请求并在同一 TCP 会话中返回响应帧。
    /// </summary>
    [Fact]
    public async Task Dlt645DownstreamRequest_IsAnsweredOnActiveConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = TestOptionsFactory.Create();
        options.Target.Protocol = LoadProtocol.Dlt6452007;
        options.Target.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        options.Devices.Count = 1;
        options.Heartbeat.IntervalSeconds = 10;
        options.Workload.ReportsEnabled = false;
        var descriptor = Assert.Single(DeviceAddressAllocator.Allocate(options));
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.Dlt6452007);
        var metrics = new LoadTestMetrics();
        metrics.SetExpectedOnline(1, DateTimeOffset.UtcNow);
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var faults = new FaultInjectionController();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = ServeDlt645DownstreamRequestAsync(listener, descriptor.MeterAddress, cancellation.Token);
        var session = new VirtualDeviceSession(
            descriptor,
            options,
            protocol,
            metrics,
            new DeviceOnlineRegistry(),
            pacer,
            gate,
            faults);
        var sessionTask = session.RunAsync(cancellation.Token);

        var response = await serverTask;
        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).DownstreamResponsesSent >= 1,
            TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await sessionTask;
        listener.Stop();

        Assert.True(response.IsValid);
        Assert.True(response.IsResponse);
        Assert.Equal(descriptor.MeterAddress, response.Address);
        Assert.Equal((byte)0x11, response.FunctionCode);
        Assert.Equal(1, metrics.Snapshot(DateTimeOffset.UtcNow).DownstreamResponsesSent);
    }

    /// <summary>
    /// 验证服务端连续关闭两次连接后，虚拟设备能够重新连接并再次完成登录。
    /// </summary>
    [Fact]
    public async Task RemoteClose_TriggersReconnectAndLoginAgain()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Target.Port = port;
        options.Heartbeat.IntervalSeconds = 10;
        options.Reconnect.MaximumAttempts = 5;
        var descriptor = Assert.Single(DeviceAddressAllocator.Allocate(options));
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var metrics = new LoadTestMetrics();
        metrics.SetExpectedOnline(1, DateTimeOffset.UtcNow);
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var faults = new FaultInjectionController();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = ServeLoginAndCloseTwiceAsync(listener, cancellation.Token);
        var session = new VirtualDeviceSession(
            descriptor,
            options,
            protocol,
            metrics,
            new DeviceOnlineRegistry(),
            pacer,
            gate,
            faults);
        var sessionTask = session.RunAsync(cancellation.Token);

        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).LoginSucceeded >= 2,
            TimeSpan.FromSeconds(5));
        await serverTask;
        cancellation.Cancel();
        await sessionTask;
        listener.Stop();

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.True(snapshot.ConnectionSucceeded >= 2);
        Assert.True(snapshot.LoginSucceeded >= 2);
        Assert.True(snapshot.ReconnectsScheduled >= 1);
        Assert.True(snapshot.RemoteClosed >= 1);
    }

    /// <summary>
    /// 验证场景编排主动取消当前连接后，设备记录注入事件并重新完成协议登录。
    /// </summary>
    [Fact]
    public async Task ForcedDisconnect_TriggersReconnectWithoutUnexpectedError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Target.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        options.Reconnect.MaximumAttempts = 5;
        var descriptor = Assert.Single(DeviceAddressAllocator.Allocate(options));
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var metrics = new LoadTestMetrics();
        metrics.SetExpectedOnline(1, DateTimeOffset.UtcNow);
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var faults = new FaultInjectionController();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = ServeTwoLoginsAsync(listener, cancellation.Token);
        var session = new VirtualDeviceSession(
            descriptor,
            options,
            protocol,
            metrics,
            new DeviceOnlineRegistry(),
            pacer,
            gate,
            faults);
        var sessionTask = session.RunAsync(cancellation.Token);

        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).LoginSucceeded >= 1,
            TimeSpan.FromSeconds(3));
        session.RequestDisconnect();
        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).LoginSucceeded >= 2,
            TimeSpan.FromSeconds(3));
        await serverTask;
        cancellation.Cancel();
        await sessionTask;
        listener.Stop();

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.ForcedDisconnects);
        Assert.True(snapshot.ReconnectsScheduled >= 1);
        Assert.DoesNotContain("SESSION_UNEXPECTED_ERROR", snapshot.Errors.Keys);
    }

    /// <summary>
    /// 验证登录报文按单字节半包发送并粘连重复帧后仍可完成登录，且注入次数可观测。
    /// </summary>
    [Fact]
    public async Task FragmentedAndDuplicatedLogin_IsObservableAndStillParsable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Target.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var descriptor = Assert.Single(DeviceAddressAllocator.Allocate(options));
        var protocol = LoadTestProtocolFactory.Create(LoadProtocol.QGdw3761);
        var metrics = new LoadTestMetrics();
        metrics.SetExpectedOnline(1, DateTimeOffset.UtcNow);
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var faults = new FaultInjectionController();
        faults.Apply(new FaultInjectionOptions
        {
            DuplicateFrameRate = 1,
            FragmentSizeBytes = 5,
            FragmentDelayMilliseconds = 1
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = ServeOneLoginAsync(listener, cancellation.Token);
        var session = new VirtualDeviceSession(
            descriptor,
            options,
            protocol,
            metrics,
            new DeviceOnlineRegistry(),
            pacer,
            gate,
            faults);
        var sessionTask = session.RunAsync(cancellation.Token);

        await WaitUntilAsync(
            () => metrics.Snapshot(DateTimeOffset.UtcNow).LoginSucceeded >= 1,
            TimeSpan.FromSeconds(5));
        await serverTask;
        cancellation.Cancel();
        await sessionTask;
        listener.Stop();

        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.True(snapshot.InjectedDuplicateFrames >= 1);
        Assert.True(snapshot.InjectedFragmentedWrites >= 1);
        Assert.Equal(1, snapshot.LoginSucceeded);
    }

    private static async Task ServeLoginAndCloseTwiceAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 2; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            var request = await ReadFrameAsync(stream, cancellationToken);
            await stream.WriteAsync(QGdw3761Codec.CreateLinkConfirmation(request), cancellationToken);
            await Task.Delay(50, cancellationToken);
        }
    }

    /// <summary>
    /// 接收一条登录和一条心跳，并按 Gather.TcpCenter 的固定 SEQ、无数据区 F1 格式分别确认。
    /// </summary>
    /// <param name="listener">回环 TCP 监听器。</param>
    /// <param name="cancellationToken">取消模拟服务的令牌。</param>
    /// <returns>完成两次协议交互的异步任务。</returns>
    private static async Task ServeGatherLoginAndHeartbeatAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var login = await ReadFrameAsync(stream, cancellationToken);
        Assert.Equal(QGdw3761LinkMessageKind.Login, login.LinkMessageKind);
        await stream.WriteAsync(CreateGatherLegacyConfirmation(login), cancellationToken);

        var heartbeat = await ReadFrameAsync(stream, cancellationToken);
        Assert.Equal(QGdw3761LinkMessageKind.Heartbeat, heartbeat.LinkMessageKind);
        await stream.WriteAsync(CreateGatherLegacyConfirmation(heartbeat), cancellationToken);
        await Task.Delay(50, cancellationToken);
    }

    /// <summary>
    /// 根据请求地址创建与 Gather.TcpCenter ConfirmDataFrame 一致的 AFN=00H/F1 老式确认帧。
    /// </summary>
    /// <param name="request">已解析的登录或心跳请求。</param>
    /// <returns>固定 SEQ=60H、无回显数据区的完整确认帧。</returns>
    private static byte[] CreateGatherLegacyConfirmation(QGdw3761Frame request)
    {
        var response = Convert.FromHexString("6832003200680B00000000020060000001000016");
        // 老服务沿用请求的四字节终端地址，但自行分配 A3 中的 MSA；确认关联不能依赖请求序号或 MSA。
        request.Raw.AsSpan(7, 4).CopyTo(response.AsSpan(7, 4));
        response[^2] = QGdw3761Codec.CalculateChecksum(response.AsSpan(6, response.Length - 8));
        return response;
    }

    private static async Task ServeTimeoutThenLoginAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using (var firstClient = await listener.AcceptTcpClientAsync(cancellationToken))
        {
            _ = await ReadFrameAsync(firstClient.GetStream(), cancellationToken);
            await Task.Delay(1_250, cancellationToken);
        }

        using var secondClient = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = secondClient.GetStream();
        var request = await ReadFrameAsync(stream, cancellationToken);
        await stream.WriteAsync(QGdw3761Codec.CreateLinkConfirmation(request), cancellationToken);
        await Task.Delay(50, cancellationToken);
    }

    private static async Task ServeTwoLoginsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        for (var index = 0; index < 2; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            var request = await ReadFrameAsync(stream, cancellationToken);
            await stream.WriteAsync(QGdw3761Codec.CreateLinkConfirmation(request), cancellationToken);
            if (index == 0)
            {
                var scratch = new byte[1];
                while (await stream.ReadAsync(scratch, cancellationToken) > 0)
                {
                }
            }
            else
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static async Task ServeOneLoginAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var request = await ReadFrameAsync(stream, cancellationToken);
        await stream.WriteAsync(QGdw3761Codec.CreateLinkConfirmation(request), cancellationToken);
        await Task.Delay(500, cancellationToken);
    }

    private static async Task<Dlt645Frame> ServeDlt645DownstreamRequestAsync(
        TcpListener listener,
        string meterAddress,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        _ = await ReadDlt645FrameAsync(stream, cancellationToken);
        // 请求控制码 11H 表示主站读数据，数据域携带四字节数据标识。
        var request = Dlt645Codec.CreateFrame(meterAddress, 0x11, [0x00, 0x00, 0x01, 0x00]);
        await stream.WriteAsync(request, cancellationToken);
        return await ReadDlt645FrameAsync(stream, cancellationToken);
    }

    private static async Task<QGdw3761Frame> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0)
                throw new IOException("客户端在发送完整登录帧之前关闭连接。");
            count += read;
            var result = QGdw3761Codec.Read(buffer.AsSpan(0, count));
            if (result.Status != FrameReadStatus.NeedMoreData)
                return Assert.IsType<QGdw3761Frame>(result.Package);
        }
    }

    private static async Task<Dlt645Frame> ReadDlt645FrameAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[512];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0)
                throw new IOException("客户端在发送完整 645 帧之前关闭连接。");
            count += read;
            var result = Dlt645Codec.Read(buffer.AsSpan(0, count));
            if (result.Status != FrameReadStatus.NeedMoreData)
                return Assert.IsType<Dlt645Frame>(result.Package);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(20, cancellation.Token);
    }
}
