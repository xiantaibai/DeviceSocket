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
        var session = new VirtualDeviceSession(descriptor, options, protocol, metrics, pacer, gate, faults);
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
        var session = new VirtualDeviceSession(descriptor, options, protocol, metrics, pacer, gate, faults);
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
        var session = new VirtualDeviceSession(descriptor, options, protocol, metrics, pacer, gate, faults);
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
        var session = new VirtualDeviceSession(descriptor, options, protocol, metrics, pacer, gate, faults);
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
        var session = new VirtualDeviceSession(descriptor, options, protocol, metrics, pacer, gate, faults);
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
