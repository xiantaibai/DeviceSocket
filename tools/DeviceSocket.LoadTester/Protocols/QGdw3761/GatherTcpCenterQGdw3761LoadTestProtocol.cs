using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.LoadTester.Configuration;
using DeviceSocket.LoadTester.Devices;

namespace DeviceSocket.LoadTester.Protocols.QGdw3761;

/// <summary>
/// 使用标准376.1报文模拟终端，并兼容 Gather.TcpCenter 老服务的非标准确认语义。
/// </summary>
internal sealed class GatherTcpCenterQGdw3761LoadTestProtocol : ILoadTestProtocol, IDownstreamFrameClassifier
{
    private readonly QGdw3761LoadTestProtocol _standardProtocol = new();
    private readonly GatherReadingResponseOptions _readingResponse;

    /// <summary>
    /// 使用指定读表应答模型创建 Gather 老服务协议适配器。
    /// </summary>
    /// <param name="readingResponse">AFN=0CH/F129 读表业务响应配置。</param>
    internal GatherTcpCenterQGdw3761LoadTestProtocol(GatherReadingResponseOptions readingResponse)
    {
        _readingResponse = readingResponse;
    }

    /// <summary>
    /// 获取当前适配器使用的基础通信协议。
    /// </summary>
    public LoadProtocol Protocol => LoadProtocol.QGdw3761;

    /// <summary>
    /// 获取是否必须等待老服务返回登录确认。
    /// </summary>
    public bool RequiresLoginConfirmation => true;

    /// <summary>
    /// 获取是否必须等待老服务返回心跳确认。
    /// </summary>
    public bool RequiresHeartbeatConfirmation => true;

    /// <summary>
    /// 创建 Gather.TcpCenter 可识别的标准376.1登录帧。
    /// </summary>
    /// <param name="device">发送登录的虚拟设备。</param>
    /// <param name="sequence">终端请求帧序号。</param>
    /// <returns>可直接写入 TCP Socket 的登录报文。</returns>
    public byte[] CreateLoginFrame(VirtualDeviceDescriptor device, byte sequence) =>
        _standardProtocol.CreateLoginFrame(device, sequence);

    /// <summary>
    /// 创建 Gather.TcpCenter 可识别的标准376.1心跳帧。
    /// </summary>
    /// <param name="device">发送心跳的虚拟设备。</param>
    /// <param name="sequence">终端请求帧序号。</param>
    /// <param name="now">写入心跳报文的终端当前时间。</param>
    /// <returns>可直接写入 TCP Socket 的心跳报文。</returns>
    public byte[] CreateHeartbeatFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) =>
        _standardProtocol.CreateHeartbeatFrame(device, sequence, now);

    /// <summary>
    /// 创建可选周期上报；一般 Gather 老服务场景只使用协议心跳，因此返回空。
    /// </summary>
    /// <param name="device">发送上报的虚拟设备。</param>
    /// <param name="sequence">终端请求帧序号。</param>
    /// <param name="now">上报发生时间。</param>
    /// <returns>当前基础兼容模式不生成独立周期上报，始终返回 null。</returns>
    public byte[]? CreateReportFrame(VirtualDeviceDescriptor device, byte sequence, DateTimeOffset now) =>
        _standardProtocol.CreateReportFrame(device, sequence, now);

    /// <summary>
    /// 创建376.1退出登录帧，场景结束时由客户端尽力发送。
    /// </summary>
    /// <param name="device">退出登录的虚拟设备。</param>
    /// <param name="sequence">终端请求帧序号。</param>
    /// <returns>可直接写入 TCP Socket 的退出登录报文。</returns>
    public byte[]? CreateLogoutFrame(VirtualDeviceDescriptor device, byte sequence) =>
        _standardProtocol.CreateLogoutFrame(device, sequence);

    /// <summary>
    /// 为老服务读表命令生成 AFN=0CH/F129 业务响应；其他要求确认的下行帧生成标准终端确认。
    /// </summary>
    /// <param name="frame">老服务下发且已完成解析的协议帧。</param>
    /// <param name="device">当前虚拟设备。</param>
    /// <param name="sequence">备用终端帧序号；标准确认会按请求 PSEQ 关联。</param>
    /// <returns>读表命令返回业务响应计划；其他下行帧合法、地址匹配且 CON=1 时返回通用确认计划，否则返回 null。</returns>
    public DownstreamResponsePlan? CreateDownstreamResponse(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        byte sequence)
    {
        if (frame is QGdw3761Frame request &&
            GatherTcpCenterReadingResponseEncoder.IsReadingRequest(request, device))
        {
            if (!_readingResponse.Enabled)
            {
                return new DownstreamResponsePlan(
                    null,
                    DownstreamResponseKind.GatherReading,
                    TimeSpan.Zero,
                    0);
            }

            var affected = IsAffectedDevice(device, _readingResponse.AffectedDevicePercentage);
            var mode = affected ? _readingResponse.Mode : GatherReadingResponseMode.Normal;
            if (mode == GatherReadingResponseMode.Drop)
            {
                return new DownstreamResponsePlan(
                    null,
                    DownstreamResponseKind.GatherReading,
                    TimeSpan.Zero,
                    0);
            }

            var businessError = mode == GatherReadingResponseMode.BusinessError;
            var payload = GatherTcpCenterReadingResponseEncoder.Create(
                request,
                device,
                _readingResponse,
                DateTimeOffset.Now,
                businessError);
            return new DownstreamResponsePlan(
                payload,
                DownstreamResponseKind.GatherReading,
                mode == GatherReadingResponseMode.Delayed
                    ? TimeSpan.FromMilliseconds(_readingResponse.DelayMilliseconds)
                    : TimeSpan.Zero,
                mode == GatherReadingResponseMode.Duplicate ? 2 : 1,
                businessError);
        }

        return _standardProtocol.CreateDownstreamResponse(frame, device, sequence);
    }

    /// <summary>
    /// 按 Gather 老服务的地址、方向、AFN 和功能单元分类下行报文；未知格式只记录，不伪装成标准 F129 读表命令。
    /// </summary>
    /// <param name="frame">已完成拆包的服务端下行帧。</param>
    /// <param name="device">收到报文的虚拟集中器。</param>
    /// <returns>可用于请求关联和未知样本聚合的结构化分类。</returns>
    public DownstreamFrameClassification ClassifyDownstream(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device)
    {
        if (frame is not QGdw3761Frame response)
        {
            return CreateClassification(
                DownstreamClassificationKind.GenericProtocolFrame,
                frame.Raw,
                false,
                false,
                null,
                null,
                null,
                [],
                [],
                null,
                "下行帧不是 Q/GDW 376.1 类型。");
        }

        var addressMatched = string.Equals(
            response.DeviceId,
            device.DeviceId,
            StringComparison.OrdinalIgnoreCase);
        if (!response.IsValid)
        {
            return CreateClassification(
                DownstreamClassificationKind.InvalidFrame,
                response.Raw,
                false,
                false,
                addressMatched,
                response.Control,
                response.Afn,
                response.Points,
                response.Functions,
                response.Sequence,
                $"376.1 帧校验失败：{response.Error?.Code}。");
        }
        if (!addressMatched)
        {
            return CreateClassification(
                DownstreamClassificationKind.AddressMismatch,
                response.Raw,
                false,
                false,
                false,
                response.Control,
                response.Afn,
                response.Points,
                response.Functions,
                response.Sequence,
                $"报文地址 {response.DeviceId ?? "<empty>"} 与会话设备 {device.DeviceId} 不一致。");
        }
        if (response.IsUpstream || !response.IsPrimaryMessage)
        {
            return CreateClassification(
                DownstreamClassificationKind.NonPrimaryDownstream,
                response.Raw,
                false,
                false,
                true,
                response.Control,
                response.Afn,
                response.Points,
                response.Functions,
                response.Sequence,
                "报文方向或 PRM 不属于主站主动下行命令。");
        }
        if (response.Afn == 0x0C && response.Functions.Count == 1 && response.Functions[0] == 129)
        {
            return CreateClassification(
                DownstreamClassificationKind.GatherReadingF129,
                response.Raw,
                true,
                false,
                true,
                response.Control,
                response.Afn,
                response.Points,
                response.Functions,
                response.Sequence,
                "标准 Gather AFN=0CH/F129 普通读表命令。");
        }
        if (response.Afn == 0x0C && response.Functions.Contains(129))
        {
            return CreateClassification(
                DownstreamClassificationKind.GatherReadingMultiFunction,
                response.Raw,
                false,
                true,
                true,
                response.Control,
                response.Afn,
                response.Points,
                response.Functions,
                response.Sequence,
                "AFN=0CH 中包含 F129 和其他功能单元，当前业务应答编码器只支持单 F129。");
        }
        if (response.Afn == 0x0C)
        {
            return CreateClassification(
                DownstreamClassificationKind.GatherAfn0CUnsupportedFunction,
                response.Raw,
                false,
                true,
                true,
                response.Control,
                response.Afn,
                response.Points,
                response.Functions,
                response.Sequence,
                "AFN=0CH 功能单元不是当前支持的单 F129，可能属于冻结、透明转发或型号专属抄读。");
        }

        return CreateClassification(
            DownstreamClassificationKind.OtherQGdw3761Command,
            response.Raw,
            false,
            true,
            true,
            response.Control,
            response.Afn,
            response.Points,
            response.Functions,
            response.Sequence,
            "有效 376.1 主站命令，但 AFN 不是当前支持的 0CH 读表格式。");
    }

    /// <summary>
    /// 创建包含稳定报文指纹的分类结果；指纹保留完整原始帧散列，避免只按 AFN/FN 把不同命令误判为重试。
    /// </summary>
    /// <param name="kind">分类类型。</param>
    /// <param name="raw">完整原始帧。</param>
    /// <param name="recognized">是否为已支持业务命令。</param>
    /// <param name="unsupported">是否为当前不支持的业务命令。</param>
    /// <param name="addressMatched">地址匹配状态。</param>
    /// <param name="control">控制域。</param>
    /// <param name="afn">AFN。</param>
    /// <param name="points">PN 集合。</param>
    /// <param name="functions">FN 集合。</param>
    /// <param name="sequence">PSEQ。</param>
    /// <param name="reason">中文分类原因。</param>
    /// <returns>不可变分类结果。</returns>
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
        string reason)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(raw));
        return new DownstreamFrameClassification
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
            Fingerprint = $"{kind}|{hash}",
            Raw = raw,
            Reason = reason
        };
    }

    /// <summary>
    /// 通过全局设备序号的稳定散列选择受故障模式影响的设备，保证多分片运行结果可复现。
    /// </summary>
    /// <param name="device">待判断的虚拟设备。</param>
    /// <param name="percentage">影响比例，单位百分比。</param>
    /// <returns>当前设备落入指定比例时返回 true。</returns>
    private static bool IsAffectedDevice(VirtualDeviceDescriptor device, double percentage)
    {
        if (percentage <= 0)
            return false;
        if (percentage >= 100)
            return true;
        // 使用与设备序号互质的乘数把连续设备均匀打散到一万个稳定桶，避免多节点各自随机导致范围漂移。
        var bucket = (int)((uint)device.GlobalOrdinal * 7_919u % 10_000u);
        return bucket < percentage * 100;
    }

    /// <summary>
    /// 判断服务端帧是否为 Gather.TcpCenter 的老式链路确认。
    /// </summary>
    /// <param name="frame">从当前 TCP 连接读取的协议帧。</param>
    /// <param name="device">正在等待确认的虚拟设备。</param>
    /// <param name="operation">正在确认的登录或心跳操作；老服务对两者返回相同结构。</param>
    /// <param name="sequence">请求序号；老服务固定返回 SEQ=60H，因此不参与关联。</param>
    /// <returns>帧合法、方向和设备地址匹配，且为无数据区 AFN=00H/F1 或 F3 时返回 true。</returns>
    public bool IsConfirmation(
        IProtocolFrame frame,
        VirtualDeviceDescriptor device,
        ProtocolOperation operation,
        byte sequence)
    {
        _ = operation;
        _ = sequence;
        if (frame is not QGdw3761Frame response ||
            !response.IsValid ||
            response.IsUpstream ||
            response.IsPrimaryMessage ||
            response.Afn != 0x00 ||
            !string.Equals(response.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase) ||
            response.Points.Count != 1 ||
            response.Points[0] != 0 ||
            response.Functions.Count != 1 ||
            response.DataUnitPayload.Length != 0)
        {
            return false;
        }

        // NormalReceiveHandle 对登录和心跳都返回 F1；部分同代 Handler 返回无数据区 F3，二者均不回显请求序号和数据单元。
        return response.Functions[0] is 1 or 3;
    }

    /// <summary>
    /// 使用共享376.1编解码器从 TCP 缓冲区读取完整帧。
    /// </summary>
    /// <param name="input">当前连接尚未消费的字节。</param>
    /// <returns>帧读取状态、消费长度和解析结果。</returns>
    public ProtocolReadResult Read(ReadOnlySpan<byte> input) => _standardProtocol.Read(input);
}
