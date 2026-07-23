using System.Net.Sockets;
using System.Text;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return;
}

var mode = args[0].ToLowerInvariant();
var host = args.ElementAtOrDefault(1) ?? "127.0.0.1";
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (mode is "3761" or "376.1")
    {
        var port = int.TryParse(args.ElementAtOrDefault(2), out var parsedPort) ? parsedPort : 5001;
        var division = args.ElementAtOrDefault(3) ?? "3301";
        var terminalAddress = ushort.TryParse(args.ElementAtOrDefault(4), out var parsedAddress) ? parsedAddress : (ushort)1;
        await Run3761Async(host, port, division, terminalAddress, cancellation.Token);
    }
    else if (mode is "645" or "645-2007")
    {
        var port = int.TryParse(args.ElementAtOrDefault(2), out var parsedPort) ? parsedPort : 5002;
        var address = args.ElementAtOrDefault(3) ?? "123456789012";
        await Run645Async(host, port, address, cancellation.Token);
    }
    else
    {
        PrintUsage();
        Environment.ExitCode = 2;
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("已停止模拟器。");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"模拟器失败：{exception.Message}");
    Environment.ExitCode = 1;
}

// 建立 376.1 连接，依次验证登录、确认、心跳和服务端主动下发链路。
static async Task Run3761Async(string host, int port, string division, ushort terminalAddress, CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(host, port, cancellationToken);
    Console.WriteLine($"已连接 376.1 服务 {host}:{port}，设备 ID 预计为 3761:{division}-{terminalAddress:D5}");
    var stream = client.GetStream();
    var receiveBuffer = new List<byte>();

    await SendAsync(stream, QGdw3761Codec.CreateLinkRequest(division, terminalAddress, QGdw3761LinkMessageKind.Login, 1), "登录", cancellationToken);
    await ReceiveOneAsync(stream, receiveBuffer, DeviceProtocol.QGdw3761, TimeSpan.FromSeconds(5), cancellationToken);

    await SendAsync(stream, QGdw3761Codec.CreateLinkRequest(division, terminalAddress, QGdw3761LinkMessageKind.Heartbeat, 2), "心跳", cancellationToken);
    await ReceiveOneAsync(stream, receiveBuffer, DeviceProtocol.QGdw3761, TimeSpan.FromSeconds(5), cancellationToken);

    Console.WriteLine("登录和心跳闭环完成，等待服务端下发；按 Ctrl+C 退出。");
    while (!cancellationToken.IsCancellationRequested)
        await ReceiveOneAsync(stream, receiveBuffer, DeviceProtocol.QGdw3761, Timeout.InfiniteTimeSpan, cancellationToken);
}

// 发送一条合法 645 应答帧完成地址绑定，随后持续接收服务端下发。
static async Task Run645Async(string host, int port, string address, CancellationToken cancellationToken)
{
    using var client = new TcpClient();
    await client.ConnectAsync(host, port, cancellationToken);
    Console.WriteLine($"已连接 645 服务 {host}:{port}，设备 ID 预计为 645:{address}");
    var stream = client.GetStream();
    var receiveBuffer = new List<byte>();
    var report = Dlt645Codec.CreateFrame(address, 0x91, [0x00, 0x00, 0x01, 0x00, 0x34, 0x12], preambleLength: 4);
    await SendAsync(stream, report, "模拟读数据正常应答", cancellationToken);

    Console.WriteLine("645 地址绑定报文已发送，等待服务端下发；按 Ctrl+C 退出。");
    while (!cancellationToken.IsCancellationRequested)
        await ReceiveOneAsync(stream, receiveBuffer, DeviceProtocol.Dlt6452007, Timeout.InfiniteTimeSpan, cancellationToken);
}

// 写入完整协议帧并输出十六进制内容，便于联调时与文档逐字节核对。
static async Task SendAsync(NetworkStream stream, byte[] frame, string name, CancellationToken cancellationToken)
{
    await stream.WriteAsync(frame, cancellationToken);
    Console.WriteLine($"> {name}: {HexCodec.ToHex(frame, separated: true)}");
}

// 从 TCP 流累积读取，直到解析出一条帧；有限等待用于登录确认，持续等待用于下发验证。
static async Task ReceiveOneAsync(
    NetworkStream stream,
    List<byte> buffered,
    DeviceProtocol protocol,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    if (timeout != Timeout.InfiniteTimeSpan)
        timeoutCancellation.CancelAfter(timeout);

    while (true)
    {
        if (TryTakePackage(buffered, protocol, out var package))
        {
            Console.WriteLine($"< {package!.Summary}: {HexCodec.ToHex(package.Raw, separated: true)}");
            return;
        }

        var chunk = new byte[4096];
        int read;
        try
        {
            read = await stream.ReadAsync(chunk, timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待服务端响应超时。");
        }

        if (read == 0)
            throw new IOException("服务端已经关闭连接。");
        buffered.AddRange(chunk.AsSpan(0, read).ToArray());
    }
}

// 按协议消费缓冲区中的一条完整帧，半包时保留所有字节等待下次读取。
static bool TryTakePackage(List<byte> buffered, DeviceProtocol protocol, out IProtocolFrame? package)
{
    package = null;
    if (buffered.Count == 0)
        return false;

    if (protocol == DeviceProtocol.QGdw3761)
    {
        var result = QGdw3761Codec.Read(buffered.ToArray());
        if (result.Status == FrameReadStatus.NeedMoreData)
            return false;
        buffered.RemoveRange(0, result.Consumed);
        package = result.Package;
        return true;
    }

    var dltResult = Dlt645Codec.Read(buffered.ToArray());
    if (dltResult.Status == FrameReadStatus.NeedMoreData)
        return false;
    buffered.RemoveRange(0, dltResult.Consumed);
    package = dltResult.Package;
    return true;
}

// 输出两种协议模拟器的最小启动参数。
static void PrintUsage()
{
    Console.WriteLine("DeviceSocket.Simulator");
    Console.WriteLine("  3761 [host=127.0.0.1] [port=5001] [行政区划=3301] [终端地址=1]");
    Console.WriteLine("  645   [host=127.0.0.1] [port=5002] [表地址=123456789012]");
}
