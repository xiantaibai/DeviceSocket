using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Globalization;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

/// <summary>
/// 模拟 GatherV4 主通讯链路中的 Q/GDW 376.1 终端，适配其 CON=1 链路确认和 Code-A3-PSEQ 命令关联规则。
/// </summary>
internal sealed class GatherV4QGdw3761LoadTestProtocol : ILoadTestProtocol, IDownstreamFrameClassifier
{
    private readonly GatherV4ResponseOptions _responseOptions;
    private readonly GatherV4ReportOptions _reportOptions;
    private readonly byte[]? _reportTemplate;

    /// <summary>使用指定终端应答故障模型创建 GatherV4 适配器。</summary>
    /// <param name="responseOptions">GatherV4 命令应答模式。</param>
    /// <param name="reportOptions">GatherV4 周期主动上报模板。</param>
    internal GatherV4QGdw3761LoadTestProtocol(
        GatherV4ResponseOptions responseOptions,
        GatherV4ReportOptions reportOptions)
    {
        _responseOptions = responseOptions;
        _reportOptions = reportOptions;
        _reportTemplate = string.IsNullOrWhiteSpace(reportOptions.FrameHexTemplate)
            ? null
            : ParseHex(reportOptions.FrameHexTemplate);
    }

    /// <summary>获取当前适配器对应的 Q/GDW 376.1 协议。</summary>
    public LoadProtocol Protocol => LoadProtocol.QGdw3761;

    /// <summary>获取是否必须收到 GatherV4 AFN=00H/F1 确认后才算业务登录成功。</summary>
    public bool RequiresLoginConfirmation => true;

    /// <summary>获取是否必须收到 GatherV4 AFN=00H/F1 确认后才算心跳健康。</summary>
    public bool RequiresHeartbeatConfirmation => true;

    /// <summary>
    /// 创建 CON=1 的登录帧；GatherV4 只有看到该标志才会生成登录 ACK 并绑定业务在线状态。
    /// </summary>
    /// <param name="device">发送登录的九位设备身份。</param>
    /// <param name="sequence">低四位 PSEQ。</param>
    /// <returns>可直接写入 Socket 的完整376.1登录帧。</returns>
    public byte[] CreateLoginFrame(VirtualDeviceDescriptor device, byte sequence) =>
        CreateLinkRequest(device, QGdw3761LinkMessageKind.Login, sequence);

    /// <summary>创建 CON=1 且携带当前时间的 GatherV4 心跳帧。</summary>
    /// <param name="device">发送心跳的九位设备身份。</param>
    /// <param name="sequence">低四位 PSEQ。</param>
    /// <param name="now">写入报文的终端当前时间。</param>
    /// <returns>可直接写入 Socket 的完整376.1心跳帧。</returns>
    public byte[] CreateHeartbeatFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) =>
        CreateLinkRequest(device, QGdw3761LinkMessageKind.Heartbeat, sequence, now);

    /// <summary>根据已验证的完整模板返回 GatherV4 主动业务上报；未配置模板时不猜测采集项并返回空。</summary>
    /// <param name="device">发送上报的设备。</param>
    /// <param name="sequence">上报 PSEQ。</param>
    /// <param name="now">上报时间。</param>
    /// <returns>已改写设备地址、PSEQ、CON 和校验和的完整帧；未配置模板时返回 null。</returns>
    public byte[]? CreateReportFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now)
    {
        _ = now;
        if (_reportTemplate is null)
            return null;

        var result = _reportTemplate.ToArray();
        // 376.1 A1 按低 BCD 字节在前，A2 按小端序；只改写设备身份，保留模板中的 A3 和具体业务数据区。
        result[7] = byte.Parse(device.AdministrativeDivision.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        result[8] = byte.Parse(device.AdministrativeDivision.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(9, 2), device.TerminalAddress);
        result[13] = (byte)((result[13] & 0xE0) |
                            (_reportOptions.ConfirmationRequested ? 0x10 : 0x00) |
                            (sequence & 0x0F));
        RecalculateChecksum(result);
        return result;
    }

    /// <summary>创建 CON=1 的退出登录帧，使服务端可观察正常下线并返回链路确认。</summary>
    /// <param name="device">退出登录的设备。</param>
    /// <param name="sequence">低四位 PSEQ。</param>
    /// <returns>可直接写入 Socket 的完整376.1退出帧。</returns>
    public byte[]? CreateLogoutFrame(VirtualDeviceDescriptor device, byte sequence) =>
        CreateLinkRequest(device, QGdw3761LinkMessageKind.Logout, sequence);

    /// <summary>
    /// 为地址匹配的 GatherV4 主站命令生成 AFN=00H/F1 全部确认；结果帧保留请求 A3 并复用 PSEQ，
    /// 既命中服务端 <c>Code-A3-PSEQ</c> 在途命令窗口，也进入 GatherV4 已实现的 F1 业务结果解析链路。
    /// </summary>
    /// <param name="frame">已经完成校验和拆包的下行帧。</param>
    /// <param name="device">当前 TCP 会话模拟的设备。</param>
    /// <param name="sequence">备用设备序号；GatherV4 关联必须复用请求 PSEQ，因此该值不参与编码。</param>
    /// <returns>可执行的终端响应计划；非本设备或非主站下行帧返回 null。</returns>
    public DownstreamResponsePlan? CreateDownstreamResponse(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        byte sequence)
    {
        _ = sequence;
        if (frame is not QGdw3761Frame request ||
            !request.IsValid ||
            request.IsUpstream ||
            !request.IsPrimaryMessage ||
            IsServerAcknowledgement(request) ||
            !string.Equals(request.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!_responseOptions.Enabled)
            return new DownstreamResponsePlan(null, DownstreamResponseKind.GatherV4Command, TimeSpan.Zero, 0);

        var affected = IsAffectedDevice(device, _responseOptions.AffectedDevicePercentage);
        var mode = affected ? _responseOptions.Mode : GatherV4ResponseMode.Normal;
        if (mode == GatherV4ResponseMode.Drop)
            return new DownstreamResponsePlan(null, DownstreamResponseKind.GatherV4Command, TimeSpan.Zero, 0);

        var payload = QGdw3761TerminalResponseEncoder.CreateAllConfirmation(request);
        if (mode == GatherV4ResponseMode.WrongA3)
        {
            // A3 的 D1-D7 是主站地址；只翻转其中一位并重算 CS，构造格式合法但不能命中当前 Mark 的响应。
            payload[11] ^= 0x02;
            RecalculateChecksum(payload);
        }
        else if (mode == GatherV4ResponseMode.WrongPseq)
        {
            // 仅修改低四位 PSEQ，保留 FIR/FIN/CON，模拟同设备返回了另一个逻辑命令的响应。
            payload[13] = (byte)((payload[13] & 0xF0) | ((payload[13] + 1) & 0x0F));
            RecalculateChecksum(payload);
        }

        return new DownstreamResponsePlan(
            payload,
            DownstreamResponseKind.GatherV4Command,
            mode == GatherV4ResponseMode.Delayed
                ? TimeSpan.FromMilliseconds(_responseOptions.DelayMilliseconds)
                : TimeSpan.Zero,
            mode == GatherV4ResponseMode.Duplicate ? 2 : 1);
    }

    /// <summary>
    /// 校验 GatherV4 对链路请求返回的 AFN=00H/F1 全部确认，要求设备、PSEQ、方向和主站启动语义完全匹配。
    /// </summary>
    /// <param name="frame">服务端返回的完整协议帧。</param>
    /// <param name="device">等待确认的虚拟设备。</param>
    /// <param name="operation">登录、心跳或退出操作；GatherV4 对这些操作使用相同的 F1 结构。</param>
    /// <param name="sequence">原始链路请求的 PSEQ。</param>
    /// <returns>确认帧可以唯一匹配当前请求时返回 true。</returns>
    public bool IsConfirmation(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        ProtocolOperation operation,
        byte sequence)
    {
        _ = operation;
        return frame is QGdw3761Frame response &&
               response.IsValid &&
               !response.IsUpstream &&
               response.IsPrimaryMessage &&
               response.Afn == 0x00 &&
               response.Sequence == (sequence & 0x0F) &&
               string.Equals(response.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase) &&
               response.Points.Count == 1 &&
               response.Points[0] == 0 &&
               response.Functions.Count == 1 &&
               response.Functions[0] == 1 &&
               response.DataUnitPayload.Length == 0;
    }

    /// <summary>按协议形态分类 GatherV4 下行帧，供命令交付、重复帧和未关联帧诊断使用。</summary>
    /// <param name="frame">已完成拆包的服务端帧。</param>
    /// <param name="device">收到该帧的虚拟设备。</param>
    /// <returns>包含 AFN、PN、FN、PSEQ 和稳定指纹的分类结果。</returns>
    public DownstreamFrameClassification ClassifyDownstream(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device)
    {
        if (frame is not QGdw3761Frame response)
            return CreateClassification(DownstreamClassificationKind.GenericProtocolFrame, frame.Raw, false, false, null, null, null, [], [], null, "下行帧不是 Q/GDW 376.1 类型。");

        var addressMatched = string.Equals(response.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase);
        if (!response.IsValid)
            return CreateClassification(DownstreamClassificationKind.InvalidFrame, response.Raw, false, false, addressMatched, response.Control, response.Afn, response.Points, response.Functions, response.Sequence, $"376.1 帧校验失败：{response.Error?.Code}。");
        if (!addressMatched)
            return CreateClassification(DownstreamClassificationKind.AddressMismatch, response.Raw, false, false, false, response.Control, response.Afn, response.Points, response.Functions, response.Sequence, $"报文地址 {response.DeviceId ?? "<empty>"} 与会话设备 {device.DeviceId} 不一致。");
        if (response.IsUpstream || !response.IsPrimaryMessage)
            return CreateClassification(DownstreamClassificationKind.NonPrimaryDownstream, response.Raw, false, false, true, response.Control, response.Afn, response.Points, response.Functions, response.Sequence, "报文方向或 PRM 不属于主站主动下行命令。");
        if (IsServerAcknowledgement(response))
            return CreateClassification(DownstreamClassificationKind.GatherV4Acknowledgement, response.Raw, false, false, true, response.Control, response.Afn, response.Points, response.Functions, response.Sequence, "GatherV4 链路或主动上报确认，不属于业务下行命令。");

        return CreateClassification(
            DownstreamClassificationKind.GatherV4Command,
            response.Raw,
            true,
            false,
            true,
            response.Control,
            response.Afn,
            response.Points,
            response.Functions,
            response.Sequence,
            $"GatherV4 主站命令，将按设备编号、A3={response.MasterStationAddress}、PSEQ={response.Sequence} 应答。");
    }

    /// <summary>使用共享376.1编解码器读取流式报文。</summary>
    /// <param name="input">当前连接尚未消费的连续字节。</param>
    /// <returns>拆包状态、消费长度和可选协议帧。</returns>
    public ProtocolReadResult Read(ReadOnlySpan<byte> input)
    {
        var result = QGdw3761Codec.Read(input);
        return new ProtocolReadResult(result.Status, result.Consumed, result.Package);
    }

    /// <summary>创建 GatherV4 要求 CON=1 的链路请求。</summary>
    /// <param name="device">目标设备身份。</param>
    /// <param name="kind">登录、心跳或退出类型。</param>
    /// <param name="sequence">低四位 PSEQ。</param>
    /// <param name="now">可选心跳时间。</param>
    /// <returns>完整376.1帧。</returns>
    private static byte[] CreateLinkRequest(
        VirtualDeviceDescriptor device,
        QGdw3761LinkMessageKind kind,
        byte sequence,
        DateTimeOffset? now = null) =>
        QGdw3761Codec.CreateLinkRequest(
            device.AdministrativeDivision,
            device.TerminalAddress,
            kind,
            sequence,
            now,
            confirmationRequested: true);

    /// <summary>根据完整376.1帧长度字段重新计算用户数据区校验和。</summary>
    /// <param name="frame">已经完成结构编码且仅 A3 或 PSEQ 被调整的完整帧。</param>
    private static void RecalculateChecksum(byte[] frame)
    {
        // 完整帧固定开销为 8 字节，因此用户数据长度可由数组长度直接得到，避免信任待故障注入字段之外的数据。
        frame[^2] = QGdw3761Codec.CalculateChecksum(frame.AsSpan(6, frame.Length - 8));
    }

    /// <summary>判断服务端帧是否为 GatherV4 AFN=00H/F1 全部确认，防止终端对 ACK 再次应答形成确认循环。</summary>
    /// <param name="frame">合法且地址匹配的376.1帧。</param>
    /// <returns>PN=0、F1 且无附加数据区时返回 true。</returns>
    private static bool IsServerAcknowledgement(QGdw3761Frame frame) =>
        frame.Afn == 0x00 &&
        frame.Points.Count == 1 &&
        frame.Points[0] == 0 &&
        frame.Functions.Count == 1 &&
        frame.Functions[0] == 1 &&
        frame.DataUnitPayload.Length == 0;

    /// <summary>把允许空格、逗号和短横线分隔的上报模板转换为完整字节数组。</summary>
    /// <param name="value">配置中的十六进制模板。</param>
    /// <returns>完整报文字节。</returns>
    private static byte[] ParseHex(string value)
    {
        var compact = string.Concat(value.Where(character =>
            !char.IsWhiteSpace(character) && character is not ',' and not '-'));
        return Convert.FromHexString(compact);
    }

    /// <summary>用全局设备序号的稳定散列选择受故障模式影响的设备，保证分片间比例一致且可复现。</summary>
    /// <param name="device">待判断虚拟设备。</param>
    /// <param name="percentage">影响比例，单位百分比。</param>
    /// <returns>设备落入指定稳定桶时返回 true。</returns>
    private static bool IsAffectedDevice(VirtualDeviceDescriptor device, double percentage)
    {
        if (percentage <= 0)
            return false;
        if (percentage >= 100)
            return true;
        var bucket = (int)((uint)device.GlobalOrdinal * 7_919u % 10_000u);
        return bucket < percentage * 100;
    }

    /// <summary>创建限量诊断分类，并通过完整原始帧散列区分协议内容不同的命令。</summary>
    /// <param name="kind">分类枚举。</param>
    /// <param name="raw">完整原始帧。</param>
    /// <param name="recognized">模拟器是否可应答。</param>
    /// <param name="unsupported">是否属于有效但不支持的业务命令。</param>
    /// <param name="addressMatched">地址是否匹配。</param>
    /// <param name="control">控制域。</param>
    /// <param name="afn">AFN。</param>
    /// <param name="points">PN 集合。</param>
    /// <param name="functions">FN 集合。</param>
    /// <param name="sequence">PSEQ。</param>
    /// <param name="reason">中文分类原因。</param>
    /// <returns>不可变诊断记录。</returns>
    private static DownstreamFrameClassification CreateClassification(
        DownstreamClassificationKind kind,
        byte[] raw,
        bool recognized,
        bool unsupported,
        bool? addressMatched,
        byte? control,
        byte? afn,
        IReadOnlyList<int> points,
        IReadOnlyList<int> functions,
        byte? sequence,
        string reason) =>
        new()
        {
            Kind = kind,
            IsRecognizedCommand = recognized,
            IsUnsupportedCommand = unsupported,
            AddressMatched = addressMatched,
            Control = control,
            Afn = afn,
            Points = points.ToArray(),
            Functions = functions.ToArray(),
            Sequence = sequence,
            RawLength = raw.Length,
            Fingerprint = $"{kind}|{Convert.ToHexString(SHA256.HashData(raw))}",
            Raw = raw,
            Reason = reason
        };
}
