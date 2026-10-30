using System.Net;
using System.Net.Sockets;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;
using DeviceSocket.LoadTester.Metrics;
using DeviceSocket.LoadTester.Protocols;
using DeviceSocket.LoadTester.Runtime;

namespace DeviceSocket.LoadTester.Tests.Runtime;

/// <summary>通过真实回环 TCP 验证纯设备模式自动确认，并保证延迟应答期间心跳继续收发。</summary>
public sealed class GatherV4DelayedResponseTests
{
    /// <summary>无 HTTP 追踪器时仍自动 F1；延迟 2.5 秒期间必须处理至少两次一秒周期心跳。</summary>
    [Fact]
    public async Task DelayedReply_DoesNotBlockHeartbeatOrRequireHttpWorkload()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = TestOptionsFactory.Create();
        options.Devices.Count = 1;
        options.Target.Host = "127.0.0.1";
        options.Target.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        options.Target.CompatibilityProfile = ServerCompatibilityProfile.GatherV4;
        options.Heartbeat.IntervalSeconds = 1;
        options.Heartbeat.JitterSeconds = 0;
        options.Heartbeat.ResponseTimeoutSeconds = 1;
        options.Workload.GatherV4Response.Mode = GatherV4ResponseMode.Delayed;
        options.Workload.GatherV4Response.DelayMilliseconds = 2500;
        options.Safety.AllowFaultInjection = true;
        var descriptor = DeviceAddressAllocator.Allocate(options)[0];
        var protocol = LoadTestProtocolFactory.Create(options);
        var metrics = new LoadTestMetrics();
        using var pacer = new ConnectionPacer(0, metrics);
        using var gate = new SemaphoreSlim(1);
        var session = new VirtualDeviceSession(descriptor, options, protocol, metrics, new DeviceOnlineRegistry(), pacer, gate, new FaultInjectionController());
        var sessionTask = session.RunAsync(cancellation.Token);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
            var stream = client.GetStream();
            using var reader = new ProtocolSocketReader(protocol);
            var heartbeats = 0;
            var answered = false;
            while (!answered)
            {
                var count = await reader.StartReadAsync(stream, cancellation.Token);
                Assert.True(count > 0);
                foreach (var frame in reader.CompleteRead(count).Cast<QGdw3761Frame>())
                {
                    if (frame.Afn == 0x02)
                    {
                        var ack = QGdw3761Codec.CreateLinkConfirmation(frame);
                        ack[6] = 0x40; ack[16] = 0x01;
                        Array.Resize(ref ack, 20);
                        ack[1] = ack[3] = 0x32;
                        ack[18] = QGdw3761Codec.CalculateChecksum(ack.AsSpan(6, 12)); ack[19] = 0x16;
                        await stream.WriteAsync(ack, cancellation.Token);
                        if (frame.LinkMessageKind == QGdw3761LinkMessageKind.Heartbeat) heartbeats++;
                        else
                        {
                            var command = frame.Raw.ToArray();
                            command[6] = 0x4B; command[11] = 0x4A; command[12] = 0x0C; command[13] = 0x69;
                            command[^2] = QGdw3761Codec.CalculateChecksum(command.AsSpan(6, command.Length - 8));
                            await stream.WriteAsync(command, cancellation.Token);
                        }
                    }
                    else if (frame.Afn == 0x00 && frame.IsUpstream)
                    {
                        Assert.Equal([1], frame.Functions);
                        Assert.Equal(9, frame.Sequence);
                        Assert.Equal(37, frame.MasterStationAddress);
                        Assert.True(heartbeats >= 2, $"延迟期间只收到 {heartbeats} 次心跳。");
                        answered = true;
                    }
                }
            }
        }
        finally
        {
            cancellation.Cancel();
            await sessionTask;
            listener.Stop();
        }
        var snapshot = metrics.Snapshot(DateTimeOffset.UtcNow);
        Assert.Equal(1, snapshot.GatherV4Commands.CorrectConfirmationsSent);
        Assert.Equal(0, snapshot.HeartbeatTimeouts);
        Assert.Equal(0, snapshot.GatherV4Commands.PendingResponses);
    }
}
