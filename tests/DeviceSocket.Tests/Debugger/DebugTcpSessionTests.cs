using System.Net;
using System.Net.Sockets;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Debugger.Core.Networking;

namespace DeviceSocket.Tests.Debugger;

/// <summary>
/// 验证真实回环 TCP 连接中的半包接收、帧解析和事件通知链路。
/// </summary>
public sealed class DebugTcpSessionTests
{
    /// <summary>
    /// 验证服务端分两次写入 376.1 帧时，调试会话只在完整后产生接收记录。
    /// </summary>
    [Fact]
    public async Task PartialTcpWrites_AreCombinedIntoOneReceiveEntry()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var logDirectory = Path.Combine(
            Path.GetTempPath(),
            "DeviceSocket.Debugger.Tests",
            Guid.NewGuid().ToString("N"));
        await using var session = new DebugTcpSession(logDirectory);
        var received = new TaskCompletionSource<DebugTrafficEntry>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.TrafficOccurred += (_, entry) =>
        {
            if (entry.Direction == TrafficDirection.Receive)
                received.TrySetResult(entry);
        };

        var acceptTask = listener.AcceptTcpClientAsync();
        await session.ConnectAsync("127.0.0.1", port, DeviceProtocol.QGdw3761);
        using var serverClient = await acceptTask;
        var frame = QGdw3761Codec.CreateLinkRequest(
            "0000",
            1,
            QGdw3761LinkMessageKind.Login,
            1);
        var serverStream = serverClient.GetStream();
        await serverStream.WriteAsync(frame.AsMemory(0, 7));
        await serverStream.WriteAsync(frame.AsMemory(7));

        var entry = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("376.1", entry.Title, StringComparison.Ordinal);
        Assert.Equal(HexCodec.ToHex(frame, true), entry.RawHex);

        await session.DisconnectAsync();
        listener.Stop();
        if (Directory.Exists(logDirectory))
            Directory.Delete(logDirectory, recursive: true);
    }
}
