using System.Buffers.Binary;
using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Core.Protocols.QGdw3761;

/// <summary>
/// 按 Q/GDW 376.1-2012 完成帧边界识别、字段解析、校验和计算及链路报文编码。
/// </summary>
public static class QGdw3761Codec
{
    public const int MaximumUserDataLength = 16_383;
    private const int FixedOverheadLength = 8;
    private const int MaximumMalformedFrameLength = 4_096;

    /// <summary>
    /// 从缓冲区头部读取一条 376.1 帧；半包不消费数据，错误帧返回可记录的错误包。
    /// </summary>
    /// <param name="input">当前连接尚未消费的字节。</param>
    public static FrameReadResult<QGdw3761Frame> Read(ReadOnlySpan<byte> input) =>
        ReadCore(input, null);

    /// <summary>
    /// 解析已经按协议长度截取的完整 376.1 帧，并在校验成功时直接把该数组交给帧模型持有。
    /// </summary>
    /// <param name="frameBytes">调用方独占且不再修改的完整帧字节数组。</param>
    /// <returns>完整帧的解析结果；头部或校验错误时返回可记录的错误帧。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frameBytes"/> 为空时抛出。</exception>
    public static FrameReadResult<QGdw3761Frame> ReadCompleteFrame(byte[] frameBytes)
    {
        ArgumentNullException.ThrowIfNull(frameBytes);
        return ReadCore(frameBytes, frameBytes);
    }

    /// <summary>
    /// 执行 376.1 帧边界和字段解析；完整帧数组由调用方独占时避免再次复制 Raw 数据。
    /// </summary>
    /// <param name="input">当前待解析字节。</param>
    /// <param name="ownedCompleteFrame">可直接转交帧模型的完整数组；普通流式读取时为空。</param>
    /// <returns>帧读取状态、消费长度和解析后的帧。</returns>
    private static FrameReadResult<QGdw3761Frame> ReadCore(ReadOnlySpan<byte> input, byte[]? ownedCompleteFrame)
    {
        if (input.IsEmpty)
            return FrameReadResult<QGdw3761Frame>.NeedMoreData();

        var startIndex = input.IndexOf((byte)0x68);
        if (startIndex < 0)
            return Invalid(ProtocolErrorCode.GarbageBeforeFrame, "未找到 68H 帧起始符。", input.ToArray(), input.Length);

        if (startIndex > 0)
            return Invalid(ProtocolErrorCode.GarbageBeforeFrame, $"帧起始符前存在 {startIndex} 个无效字节。", input[..startIndex].ToArray(), startIndex);

        if (input.Length < 6)
            return FrameReadResult<QGdw3761Frame>.NeedMoreData();

        if (input[5] != 0x68)
            return InvalidHeader(input, ProtocolErrorCode.InvalidSecondStart, "第二个帧起始符不是 68H。");

        // 文档规定两个 L 均按低字节在前传输，且必须完全相同。
        var firstLength = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(1, 2));
        var secondLength = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(3, 2));
        if (firstLength != secondLength)
            return InvalidHeader(input, ProtocolErrorCode.LengthMismatch, "两个长度 L 不一致。");

        if ((firstLength & 0b11) != 0b10)
            return InvalidHeader(input, ProtocolErrorCode.UnsupportedProtocolIdentifier, "长度字段 D0-D1 不是本协议要求的 10b。");

        // L 的 D0-D1 是协议标识，实际用户数据长度存放在 D2-D15。
        var userDataLength = firstLength >> 2;
        if (userDataLength < 8)
            return InvalidHeader(input, ProtocolErrorCode.InvalidLength, "用户数据区长度小于最小链路报文长度。");

        if (userDataLength > MaximumUserDataLength)
            return InvalidHeader(input, ProtocolErrorCode.FrameTooLarge, "用户数据区超过文档规定的网络传输上限。");

        var totalLength = userDataLength + FixedOverheadLength;
        if (input.Length < totalLength)
            return FrameReadResult<QGdw3761Frame>.NeedMoreData();

        var raw = ownedCompleteFrame is not null && ownedCompleteFrame.Length == totalLength
            ? ownedCompleteFrame
            : input[..totalLength].ToArray();
        if (raw[^1] != 0x16)
            return Invalid(ProtocolErrorCode.InvalidEnd, "帧结束符不是 16H。", raw, totalLength);

        // CS 只覆盖用户数据区：控制域 C、地址域 A 和链路用户数据。
        var expectedChecksum = CalculateChecksum(raw.AsSpan(6, userDataLength));
        if (raw[^2] != expectedChecksum)
            return Invalid(ProtocolErrorCode.InvalidChecksum, $"校验和错误，收到 {raw[^2]:X2}，计算值 {expectedChecksum:X2}。", raw, totalLength);

        if (!TryDecodeAdministrativeDivision(raw[7], raw[8], out var administrativeDivision))
            return Invalid(ProtocolErrorCode.InvalidAddress, "行政区划码不是合法 BCD。", raw, totalLength);

        var terminalAddress = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(9, 2));
        var a3 = raw[11];
        var afn = raw[12];
        var sequenceByte = raw[13];
        var hasDataUnitIdentifier = userDataLength >= 12;
        var da1 = hasDataUnitIdentifier ? raw[14] : (byte)0;
        var da2 = hasDataUnitIdentifier ? raw[15] : (byte)0;
        var dt1 = hasDataUnitIdentifier ? raw[16] : (byte)0;
        var dt2 = hasDataUnitIdentifier ? raw[17] : (byte)0;
        var functions = hasDataUnitIdentifier ? DecodeFunctions(dt1, dt2) : [];
        var points = hasDataUnitIdentifier ? DecodePoints(da1, da2) : [];
        var payloadLength = Math.Max(0, userDataLength - 12);
        var payload = payloadLength == 0 ? [] : raw.AsSpan(18, payloadLength).ToArray();
        var kind = ResolveLinkMessageKind(afn, points, functions);
        var deviceId = $"3761:{administrativeDivision}-{terminalAddress:D5}";

        var frame = new QGdw3761Frame
        {
            IsValid = true,
            Raw = raw,
            DeviceId = deviceId,
            UserDataLength = userDataLength,
            Control = raw[6],
            IsUpstream = (raw[6] & 0x80) != 0,
            IsPrimaryMessage = (raw[6] & 0x40) != 0,
            LinkFunctionCode = (byte)(raw[6] & 0x0F),
            AdministrativeDivision = administrativeDivision,
            TerminalAddress = terminalAddress,
            MasterStationAddress = (byte)(a3 >> 1),
            IsGroupAddress = (a3 & 0x01) != 0,
            Afn = afn,
            SequenceByte = sequenceByte,
            HasTimeTag = (sequenceByte & 0x80) != 0,
            IsFirstFrame = (sequenceByte & 0x40) != 0,
            IsFinalFrame = (sequenceByte & 0x20) != 0,
            ConfirmationRequested = (sequenceByte & 0x10) != 0,
            Sequence = (byte)(sequenceByte & 0x0F),
            Da1 = da1,
            Da2 = da2,
            Dt1 = dt1,
            Dt2 = dt2,
            Points = points,
            Functions = functions,
            DataUnitPayload = payload,
            LinkMessageKind = kind
        };

        return FrameReadResult<QGdw3761Frame>.Ready(frame, totalLength);
    }

    /// <summary>
    /// 为登录、退出或心跳请求创建 AFN=00H、F3 的逐数据单元确认帧。
    /// </summary>
    /// <param name="request">需要确认的合法上行帧。</param>
    /// <param name="errorCode">附录 D 中定义的 ERR，0 表示正确。</param>
    public static byte[] CreateLinkConfirmation(QGdw3761Frame request, byte errorCode = 0)
    {
        if (!request.IsValid || request.Raw.Length < 18)
            throw new ArgumentException("只能为包含数据单元标识的合法 376.1 帧创建确认。", nameof(request));

        var application = new byte[]
        {
            0x00,
            (byte)(0x60 | request.Sequence),
            0x00, 0x00, 0x04, 0x00,
            request.Afn,
            request.Da1, request.Da2, request.Dt1, request.Dt2,
            errorCode
        };

        return BuildFrame(0x00, request.Raw.AsSpan(7, 5), application);
    }

    /// <summary>
    /// 为需要设备确认的下行报文写入 PSEQ（启动帧序号）并置 CON=1，然后重新计算校验和。
    /// </summary>
    /// <param name="command">已通过完整性和校验和检查的主站下行启动帧。</param>
    /// <param name="sequence">本次逻辑命令的 PSEQ，仅使用低 4 位。</param>
    /// <returns>可直接写入 Socket 的新报文；不修改输入帧持有的 Raw。</returns>
    /// <exception cref="ArgumentException">报文不是合法的主站下行启动帧时抛出。</exception>
    public static byte[] PrepareReliableDownstreamCommand(QGdw3761Frame command, byte sequence)
    {
        if (!command.IsValid || command.IsUpstream || !command.IsPrimaryMessage ||
            command.Raw.Length != command.UserDataLength + FixedOverheadLength || command.Raw.Length < 18)
        {
            throw new ArgumentException("只能处理包含数据单元标识的合法 376.1 主站下行启动帧。", nameof(command));
        }

        var result = command.Raw.ToArray();
        // SEQ 高 3 位的 TpV/FIR/FIN 保持原值；可靠命令必须置 CON=1，低 4 位由 Gateway 统一分配 PSEQ。
        result[13] = (byte)((result[13] & 0xE0) | 0x10 | (sequence & 0x0F));
        result[^2] = CalculateChecksum(result.AsSpan(6, command.UserDataLength));
        return result;
    }

    /// <summary>
    /// 为主站下行启动帧生成终端 AFN=00H/F3 逐数据单元确认，RSEQ 复用请求的 PSEQ。
    /// </summary>
    /// <param name="request">终端已收到的合法主站下行启动帧。</param>
    /// <param name="errorCode">协议附录 D 定义的 ERR；0 表示正确，非 0 表示否认。</param>
    /// <returns>终端应向主站发送的完整确认帧。</returns>
    /// <exception cref="ArgumentException">请求不是合法主站下行帧时抛出。</exception>
    public static byte[] CreateTerminalConfirmation(QGdw3761Frame request, byte errorCode = 0)
    {
        if (!request.IsValid || request.IsUpstream || !request.IsPrimaryMessage || request.Raw.Length < 18)
            throw new ArgumentException("只能确认合法的 376.1 主站下行启动帧。", nameof(request));

        var application = new byte[]
        {
            0x00,
            (byte)(0x60 | request.Sequence),
            0x00, 0x00, 0x04, 0x00,
            request.Afn,
            request.Da1, request.Da2, request.Dt1, request.Dt2,
            errorCode
        };

        // DIR=1 表示终端上行，PRM=0 表示从动响应，功能码 0 用于确认。
        return BuildFrame(0x80, request.Raw.AsSpan(7, 5), application);
    }

    /// <summary>
    /// 为主站下行启动帧生成终端 AFN=00H/F1 全部确认或 F2 全部否认，RSEQ 复用请求的 PSEQ。
    /// </summary>
    /// <param name="request">终端已收到的合法主站下行启动帧。</param>
    /// <param name="accepted">true 生成 F1 全部确认；false 生成 F2 全部否认。</param>
    /// <returns>终端应向主站发送的完整任务结果帧。</returns>
    /// <exception cref="ArgumentException">请求不是合法的主站下行帧时抛出。</exception>
    public static byte[] CreateTerminalAllResult(QGdw3761Frame request, bool accepted = true)
    {
        if (!request.IsValid || request.IsUpstream || !request.IsPrimaryMessage || request.Raw.Length < 18)
            throw new ArgumentException("只能回复合法的 376.1 主站下行启动帧。", nameof(request));

        var application = new byte[]
        {
            0x00,
            (byte)(0x60 | request.Sequence),
            0x00, 0x00, accepted ? (byte)0x01 : (byte)0x02, 0x00
        };

        // GatherV4 以 Code-A3-PSEQ 关联在途任务，因此必须原样复用地址域（包含 A3）和 PSEQ。
        return BuildFrame(0x80, request.Raw.AsSpan(7, 5), application);
    }

    /// <summary>
    /// 创建供模拟终端使用的 AFN=02H 登录、退出登录或心跳上行帧。
    /// </summary>
    /// <param name="administrativeDivision">4 位 BCD 行政区划码。</param>
    /// <param name="terminalAddress">1-65535 的终端地址。</param>
    /// <param name="kind">链路接口检测业务类型。</param>
    /// <param name="sequence">低 4 位帧序号。</param>
    /// <param name="heartbeatTime">心跳中的终端时钟；为空时使用当前时间。</param>
    /// <param name="confirmationRequested">是否设置 CON=1 请求主站确认；默认 false 兼容现有 DeviceSocket 和 Gather.TcpCenter 行为。</param>
    public static byte[] CreateLinkRequest(
        string administrativeDivision,
        ushort terminalAddress,
        QGdw3761LinkMessageKind kind,
        byte sequence = 0,
        DateTimeOffset? heartbeatTime = null,
        bool confirmationRequested = false)
    {
        var function = kind switch
        {
            QGdw3761LinkMessageKind.Login => 1,
            QGdw3761LinkMessageKind.Logout => 2,
            QGdw3761LinkMessageKind.Heartbeat => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), "只支持登录、退出登录和心跳链路请求。")
        };

        var address = new byte[5];
        EncodeAdministrativeDivision(administrativeDivision, address);
        BinaryPrimitives.WriteUInt16LittleEndian(address.AsSpan(2, 2), terminalAddress);

        var application = new List<byte>
        {
            0x02,
            // GatherV4 仅在 CON=1 时生成登录和心跳 ACK；其他服务端继续使用默认 CON=0，避免改变既有协议行为。
            (byte)(0x60 | (confirmationRequested ? 0x10 : 0x00) | (sequence & 0x0F)),
            0x00, 0x00,
            (byte)(1 << (function - 1)), 0x00
        };

        if (kind == QGdw3761LinkMessageKind.Heartbeat)
        {
            var time = heartbeatTime ?? DateTimeOffset.Now;
            application.Add(ToBcd(time.Second));
            application.Add(ToBcd(time.Minute));
            application.Add(ToBcd(time.Hour));
            application.Add(ToBcd(time.Day));
            application.Add(ToBcd(time.Month));
            application.Add(ToBcd(time.Year % 100));
        }

        return BuildFrame(0xC9, address, application.ToArray());
    }

    /// <summary>
    /// 计算用户数据区所有字节模 256 的算术和。
    /// </summary>
    public static byte CalculateChecksum(ReadOnlySpan<byte> userData)
    {
        var sum = 0;
        foreach (var value in userData)
            sum += value;
        return unchecked((byte)sum);
    }

    private static byte[] BuildFrame(byte control, ReadOnlySpan<byte> address, ReadOnlySpan<byte> application)
    {
        if (address.Length != 5)
            throw new ArgumentException("376.1 地址域必须为 5 字节。", nameof(address));

        var userDataLength = 1 + address.Length + application.Length;
        // 编码时把用户数据长度左移两位，并写入本协议标识 10b。
        var lengthField = checked((ushort)((userDataLength << 2) | 0b10));
        var frame = new byte[userDataLength + FixedOverheadLength];
        frame[0] = 0x68;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), lengthField);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3, 2), lengthField);
        frame[5] = 0x68;
        frame[6] = control;
        address.CopyTo(frame.AsSpan(7, 5));
        application.CopyTo(frame.AsSpan(12));
        frame[^2] = CalculateChecksum(frame.AsSpan(6, userDataLength));
        frame[^1] = 0x16;
        return frame;
    }

    private static FrameReadResult<QGdw3761Frame> Invalid(ProtocolErrorCode code, string message, byte[] raw, int consumed) =>
        FrameReadResult<QGdw3761Frame>.Ready(QGdw3761Frame.Invalid(code, message, raw), consumed);

    /// <summary>
    /// 隔离固定报文头损坏产生的整段异常候选帧，避免逐字节重同步造成重复错误日志。
    /// </summary>
    private static FrameReadResult<QGdw3761Frame> InvalidHeader(
        ReadOnlySpan<byte> input,
        ProtocolErrorCode code,
        string message)
    {
        var boundary = FindMalformedFrameBoundary(input);
        if (boundary == 0 && input.Length < MaximumMalformedFrameLength)
            return FrameReadResult<QGdw3761Frame>.NeedMoreData();

        boundary = boundary > 0 ? boundary : Math.Min(input.Length, MaximumMalformedFrameLength);
        return Invalid(code, $"{message} 已整体隔离 {boundary} 字节异常候选帧。", input[..boundary].ToArray(), boundary);
    }

    /// <summary>
    /// 优先保护后续合法帧；找不到合法帧头时，再使用校验和与 16H 确定异常候选帧边界。
    /// </summary>
    private static int FindMalformedFrameBoundary(ReadOnlySpan<byte> input)
    {
        var nextFrameStart = FindNextStandardFrameStart(input);
        if (nextFrameStart > 0)
            return nextFrameStart;

        // 某些错误报文只有一个 L，并把第二个 68H 放在偏移 3；校验和仍从偏移 4 开始计算。
        var userDataStart = input.Length > 3 && input[3] == 0x68 ? 4 : 6;
        var checksumBoundary = FindChecksumValidatedEnd(input, userDataStart);
        if (checksumBoundary > 0)
            return checksumBoundary;

        // 校验和也损坏时，以第一个结束符作为最后的有界恢复依据。
        var endOffset = input.Length > 6 ? input[6..].IndexOf((byte)0x16) : -1;
        return endOffset >= 0 ? endOffset + 7 : 0;
    }

    /// <summary>
    /// 查找异常数据之后下一个结构完整的标准 376.1 固定报文头。
    /// </summary>
    private static int FindNextStandardFrameStart(ReadOnlySpan<byte> input)
    {
        for (var index = 1; index + 5 < input.Length; index++)
        {
            if (input[index] != 0x68 || input[index + 5] != 0x68)
                continue;

            var firstLength = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(index + 1, 2));
            var secondLength = BinaryPrimitives.ReadUInt16LittleEndian(input.Slice(index + 3, 2));
            var userDataLength = firstLength >> 2;
            if (firstLength == secondLength &&
                (firstLength & 0b11) == 0b10 &&
                userDataLength is >= 8 and <= MaximumUserDataLength)
                return index;
        }

        return 0;
    }

    /// <summary>
    /// 在未知长度的异常候选帧中查找校验和正确的 CS、16H 结束位置。
    /// </summary>
    private static int FindChecksumValidatedEnd(ReadOnlySpan<byte> input, int userDataStart)
    {
        const int minimumUserDataLength = 8;
        var firstEndIndex = userDataStart + minimumUserDataLength + 1;
        for (var endIndex = firstEndIndex; endIndex < input.Length; endIndex++)
        {
            if (input[endIndex] != 0x16)
                continue;

            var checksumIndex = endIndex - 1;
            var userDataLength = checksumIndex - userDataStart;
            if (input[checksumIndex] == CalculateChecksum(input.Slice(userDataStart, userDataLength)))
                return endIndex + 1;
        }

        return 0;
    }

    private static bool TryDecodeAdministrativeDivision(byte lowByte, byte highByte, out string division)
    {
        division = string.Empty;
        if (!IsBcd(lowByte) || !IsBcd(highByte))
            return false;

        division = $"{highByte >> 4}{highByte & 0x0F}{lowByte >> 4}{lowByte & 0x0F}";
        return true;
    }

    private static void EncodeAdministrativeDivision(string value, Span<byte> destination)
    {
        if (value.Length != 4 || value.Any(c => !char.IsDigit(c)))
            throw new ArgumentException("行政区划码必须为 4 位十进制数字。", nameof(value));

        destination[0] = (byte)(((value[2] - '0') << 4) | (value[3] - '0'));
        destination[1] = (byte)(((value[0] - '0') << 4) | (value[1] - '0'));
    }

    private static bool IsBcd(byte value) => (value >> 4) <= 9 && (value & 0x0F) <= 9;
    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static IReadOnlyList<int> DecodeFunctions(byte value, byte group)
    {
        var result = new List<int>(8);
        for (var bit = 0; bit < 8; bit++)
            if ((value & (1 << bit)) != 0)
                result.Add(group * 8 + bit + 1);
        return result;
    }

    private static IReadOnlyList<int> DecodePoints(byte value, byte group)
    {
        if (value == 0 && group == 0)
            return [0];
        if (value == 0xFF && group == 0)
            return [-1];
        if (group == 0)
            return [];

        var result = new List<int>(8);
        for (var bit = 0; bit < 8; bit++)
            if ((value & (1 << bit)) != 0)
                result.Add((group - 1) * 8 + bit + 1);
        return result;
    }

    private static QGdw3761LinkMessageKind ResolveLinkMessageKind(byte afn, IReadOnlyList<int> points, IReadOnlyList<int> functions)
    {
        if (afn != 0x02 || points.Count != 1 || points[0] != 0 || functions.Count != 1)
            return QGdw3761LinkMessageKind.Other;

        return functions[0] switch
        {
            1 => QGdw3761LinkMessageKind.Login,
            2 => QGdw3761LinkMessageKind.Logout,
            3 => QGdw3761LinkMessageKind.Heartbeat,
            _ => QGdw3761LinkMessageKind.Other
        };
    }
}
