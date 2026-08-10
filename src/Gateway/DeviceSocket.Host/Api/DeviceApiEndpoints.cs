using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;
using DeviceSocket.Core.Runtime.Connections;
using DeviceSocket.Core.Runtime.Messages;
using DeviceSocket.Core.Runtime.Metrics;
using DeviceSocket.Host.Api.Models;
using DeviceSocket.Host.Configuration.Logging;
using DeviceSocket.Host.Networking.Common;
using DeviceSocket.Host.Observability;

namespace DeviceSocket.Host.Api;

/// <summary>
/// 统一注册设备网关的状态查询、调试解析、消息查询和指定设备下发 API。
/// </summary>
public static class DeviceApiEndpoints
{
    /// <summary>
    /// 把 DeviceSocket Demo 的全部 HTTP API 映射到当前路由端点集合。
    /// </summary>
    /// <param name="endpoints">ASP.NET Core 路由端点集合。</param>
    /// <returns>用于继续链式配置的原路由端点集合。</returns>
    public static IEndpointRouteBuilder MapDeviceApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Ok(new
        {
            service = "DeviceSocket Demo",
            runtime = ".NET 8",
            protocols = new[] { "Q/GDW 376.1-2012", "DL/T 645-2007" },
            swagger = "/swagger",
            endpoints = new[]
                {
                    "GET /api/status/connections",
                    "GET /api/devices",
                    "GET /api/messages/recent",
                    "GET /api/errors/recent",
                    "POST /api/devices/{deviceId}/messages",
                    "POST /api/tools/decode"
                }
        }))
            .WithName("GetServiceInformation")
            .WithTags("服务")
            .WithSummary("获取服务基本信息")
            .Produces(StatusCodes.Status200OK);

        endpoints.MapGet("/api/status/connections", (
            IOnlineDeviceRegistry registry,
            RuntimeMetrics metrics,
            DeviceConnectionLimiter limiter,
            DeviceTcpSessionManager sessionManager,
            GatewayProcessMetrics processMetrics,
            DeviceTrafficLoggingMetrics trafficLoggingMetrics,
            LogBufferMonitorRegistry logBufferMonitors) =>
        {
            var process = processMetrics.Capture();
            var applicationLog = logBufferMonitors.Capture(LogBufferMonitorRegistry.ApplicationLogType);
            var upstreamLog = logBufferMonitors.Capture(DeviceTrafficLogCategories.UpstreamLogType);
            var downstreamLog = logBufferMonitors.Capture(DeviceTrafficLogCategories.DownstreamLogType);
            return Results.Ok(new
            {
                tcpConnections = metrics.TcpConnections,
                peakTcpConnections = metrics.PeakTcpConnections,
                openedConnections = metrics.OpenedConnections,
                closedConnections = metrics.ClosedConnections,
                authenticatedDevices = registry.Count,
                trackedSessions = sessionManager.SessionCount,
                authenticatedSessions = sessionManager.AuthenticatedSessionCount,
                unauthenticatedConnections = limiter.UnauthenticatedConnectionCount,
                protocol3761Connections = metrics.QGdw3761Connections,
                protocol645Connections = metrics.Dlt645Connections,
                rejectedConnections = limiter.RejectedConnections,
                capacityRejections = limiter.CapacityRejections,
                unauthenticatedCapacityRejections = limiter.UnauthenticatedCapacityRejections,
                perIpCapacityRejections = limiter.PerIpCapacityRejections,
                rateLimitRejections = limiter.RateLimitRejections,
                pendingSendMessages = sessionManager.PendingSendMessages,
                pendingSendBytes = sessionManager.PendingSendBytes,
                peakPendingSendMessages = sessionManager.PeakPendingSendMessages,
                peakPendingSendBytes = sessionManager.PeakPendingSendBytes,
                loginTimeoutClosures = sessionManager.LoginTimeoutClosures,
                idleTimeoutClosures = sessionManager.IdleTimeoutClosures,
                protocolErrorClosures = sessionManager.ProtocolErrorClosures,
                duplicateLoginClosures = sessionManager.DuplicateLoginClosures,
                logoutClosures = sessionManager.LogoutClosures,
                sendTimeoutClosures = sessionManager.SendTimeoutClosures,
                sendBackpressureClosures = sessionManager.SendBackpressureClosures,
                staleSessionClosures = sessionManager.StaleSessionClosures,
                receivedMessages = metrics.ReceivedMessages,
                sentMessages = metrics.SentMessages,
                sendFailures = metrics.SendFailures,
                protocolErrors = metrics.ProtocolErrors,
                heartbeatsReceived = metrics.HeartbeatsReceived,
                heartbeatConfirmationsSent = metrics.HeartbeatConfirmationsSent,
                sessionScanCount = sessionManager.SessionScanCount,
                lastSessionScanAt = sessionManager.LastSessionScanAt,
                lastSessionScanDurationMilliseconds = sessionManager.LastSessionScanDurationMilliseconds,
                maximumSessionScanDurationMilliseconds = sessionManager.MaximumSessionScanDurationMilliseconds,
                lastSessionScanSessions = sessionManager.LastSessionScanSessions,
                lastSessionScanCandidates = sessionManager.LastSessionScanCandidates,
                lastSessionScanClosed = sessionManager.LastSessionScanClosed,
                heartbeatLogSuppressed = trafficLoggingMetrics.HeartbeatSuppressed,
                heartbeatLogRecordedByDevice = trafficLoggingMetrics.HeartbeatRecordedByDevice,
                heartbeatLogRecordedBySampling = trafficLoggingMetrics.HeartbeatRecordedBySampling,
                upstreamLogSubmitted = trafficLoggingMetrics.UpstreamSubmitted,
                downstreamLogSubmitted = trafficLoggingMetrics.DownstreamSubmitted,
                trafficLogSynchronousFailures = trafficLoggingMetrics.SynchronousWriteFailures,
                applicationLogCount = applicationLog.Count,
                applicationLogBufferSize = applicationLog.BufferSize,
                applicationLogDropped = applicationLog.DroppedMessagesCount,
                upstreamLogCount = upstreamLog.Count,
                upstreamLogBufferSize = upstreamLog.BufferSize,
                upstreamLogDropped = upstreamLog.DroppedMessagesCount,
                downstreamLogCount = downstreamLog.Count,
                downstreamLogBufferSize = downstreamLog.BufferSize,
                downstreamLogDropped = downstreamLog.DroppedMessagesCount,
                processCpuPercent = process.CpuPercent,
                processWorkingSetBytes = process.WorkingSetBytes,
                managedHeapBytes = process.ManagedHeapBytes,
                gcCommittedBytes = process.GcCommittedBytes,
                gcPauseTimePercentage = process.GcPauseTimePercentage,
                gen0Collections = process.Gen0Collections,
                gen1Collections = process.Gen1Collections,
                gen2Collections = process.Gen2Collections,
                threadPoolThreads = process.ThreadPoolThreads,
                threadPoolPendingWorkItems = process.ThreadPoolPendingWorkItems,
                threadPoolAvailableWorkerThreads = process.ThreadPoolAvailableWorkerThreads,
                threadPoolAvailableIoThreads = process.ThreadPoolAvailableIoThreads,
                processHandleCount = process.HandleCount,
                processUptimeSeconds = process.UptimeSeconds,
                timestamp = DateTimeOffset.UtcNow
            });
        })
            .WithName("GetConnectionStatus")
            .WithTags("运行状态")
            .WithSummary("查询当前连接数、在线设备数和收发指标")
            .Produces(StatusCodes.Status200OK);

        endpoints.MapGet("/api/devices", (int? take, IOnlineDeviceRegistry registry) =>
                Results.Ok(registry.GetSnapshot(Math.Clamp(take ?? 100, 1, 1000))))
            .WithName("GetOnlineDevices")
            .WithTags("设备")
            .WithSummary("查询当前在线设备")
            .Produces<IReadOnlyList<DeviceConnectionSnapshot>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/messages/recent", (int? take, IRecentStore<ReceivedDeviceMessage> store) =>
                Results.Ok(store.GetRecent(Math.Clamp(take ?? 100, 1, 1000))))
            .WithName("GetRecentMessages")
            .WithTags("消息")
            .WithSummary("查询最近收到的合法设备消息")
            .Produces<IReadOnlyList<ReceivedDeviceMessage>>(StatusCodes.Status200OK);

        endpoints.MapGet("/api/errors/recent", (int? take, IRecentStore<DeviceErrorEvent> store) =>
                Results.Ok(store.GetRecent(Math.Clamp(take ?? 100, 1, 1000))))
            .WithName("GetRecentErrors")
            .WithTags("错误")
            .WithSummary("查询最近的协议、处理和下发错误")
            .Produces<IReadOnlyList<DeviceErrorEvent>>(StatusCodes.Status200OK);

        endpoints.MapPost("/api/tools/decode", (DecodeRequest request) =>
        {
            if (!HexCodec.TryParse(request.Hex, out var bytes, out var hexError))
                return ApiProblem(400, "INVALID_HEX", hexError!);

            if (!TryParseProtocol(request.Protocol, out var protocol))
                return ApiProblem(400, "INVALID_PROTOCOL", "protocol 仅支持 376.1 或 645。");

            var validation = ValidateFrame(protocol, bytes);
            if (!validation.Success)
                return ApiProblem(400, validation.Code!, validation.Message!);
            return Results.Ok(ToDecodeResponse(validation.Frame!));
        })
            .WithName("DecodeProtocolFrame")
            .WithTags("协议工具")
            .WithSummary("解析并校验一条 376.1 或 645 十六进制报文")
            .Accepts<DecodeRequest>("application/json")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        endpoints.MapPost("/api/devices/{deviceId}/messages", SendDeviceMessageAsync)
            .WithName("SendDeviceMessage")
            .WithTags("设备")
            .WithSummary("向指定在线设备下发一条完整协议帧")
            .Accepts<SendMessageRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    /// <summary>
    /// 校验目标设备、协议及完整帧后，把原始二进制消息写入当前在线连接。
    /// </summary>
    private static async Task<IResult> SendDeviceMessageAsync(
        string deviceId,
        SendMessageRequest request,
        HttpContext context,
        IOnlineDeviceRegistry registry,
        IRecentStore<DeviceErrorEvent> errors,
        RuntimeMetrics metrics,
        CancellationToken cancellationToken)
    {
        if (!registry.TryGetConnection(deviceId, out var connection) || connection is null)
            return ApiProblem(404, "DEVICE_OFFLINE", "设备当前不在线。", context.TraceIdentifier);

        if (!HexCodec.TryParse(request.Hex, out var bytes, out var hexError))
            return ApiProblem(400, "INVALID_HEX", hexError!, context.TraceIdentifier);

        var protocol = connection.Protocol;
        if (!string.IsNullOrWhiteSpace(request.Protocol))
        {
            if (!TryParseProtocol(request.Protocol, out var requestedProtocol))
                return ApiProblem(400, "INVALID_PROTOCOL", "protocol 仅支持 376.1 或 645。", context.TraceIdentifier);
            if (requestedProtocol != protocol)
                return ApiProblem(409, "PROTOCOL_MISMATCH", "下发协议与设备连接协议不一致。", context.TraceIdentifier);
        }

        var validation = ValidateFrame(protocol, bytes);
        if (!validation.Success)
            return ApiProblem(400, validation.Code!, validation.Message!, context.TraceIdentifier);

        if (!string.Equals(validation.Frame!.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            return ApiProblem(409, "DEVICE_ADDRESS_MISMATCH", $"帧内设备地址 {validation.Frame.DeviceId} 与目标设备不一致。", context.TraceIdentifier);

        try
        {
            await connection.SendAsync(bytes, cancellationToken);
            metrics.MessageSent();
            return Results.Accepted(value: new
            {
                success = true,
                code = "MESSAGE_SENT",
                deviceId,
                bytes = bytes.Length,
                traceId = context.TraceIdentifier
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            metrics.SendFailed();
            errors.Add(new DeviceErrorEvent(
                DateTimeOffset.UtcNow,
                "Send",
                "DEVICE_SEND_FAILED",
                exception.Message,
                protocol,
                deviceId,
                connection.SessionId,
                connection.RemoteEndPoint,
                HexCodec.ToHex(bytes.AsSpan(0, Math.Min(bytes.Length, 256))),
                context.TraceIdentifier));
            return ApiProblem(503, "DEVICE_SEND_FAILED", "消息下发失败，连接可能已经关闭。", context.TraceIdentifier);
        }
    }

    /// <summary>
    /// 接受便于人工调试的协议别名，并统一转换为内部协议枚举。
    /// </summary>
    private static bool TryParseProtocol(string? value, out DeviceProtocol protocol)
    {
        if (value is not null && (value.Equals("376.1", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("3761", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("QGdw3761", StringComparison.OrdinalIgnoreCase)))
        {
            protocol = DeviceProtocol.QGdw3761;
            return true;
        }

        if (value is not null && (value.Equals("645", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("645-2007", StringComparison.OrdinalIgnoreCase) ||
                                  value.Equals("Dlt6452007", StringComparison.OrdinalIgnoreCase)))
        {
            protocol = DeviceProtocol.Dlt6452007;
            return true;
        }

        protocol = default;
        return false;
    }

    /// <summary>
    /// 下发前验证输入仅包含一条完整合法帧，拒绝粘包和尾随垃圾数据。
    /// </summary>
    private static FrameValidation ValidateFrame(DeviceProtocol protocol, byte[] bytes)
    {
        if (protocol == DeviceProtocol.QGdw3761)
        {
            var result = QGdw3761Codec.Read(bytes);
            if (result.Status == FrameReadStatus.NeedMoreData)
                return FrameValidation.Fail("INCOMPLETE_FRAME", "376.1 报文不完整。");
            if (result.Package is not { IsValid: true } frame)
                return FrameValidation.Fail(result.Package?.Error?.Code.ToString() ?? "INVALID_FRAME", result.Package?.Error?.Message ?? "376.1 报文无效。");
            if (result.Consumed != bytes.Length)
                return FrameValidation.Fail("MULTIPLE_OR_TRAILING_FRAMES", "下发接口一次只允许一个完整帧。");
            return FrameValidation.Ok(frame);
        }

        var dltResult = Dlt645Codec.Read(bytes);
        if (dltResult.Status == FrameReadStatus.NeedMoreData)
            return FrameValidation.Fail("INCOMPLETE_FRAME", "645 报文不完整。");
        if (dltResult.Package is not { IsValid: true } dltFrame)
            return FrameValidation.Fail(dltResult.Package?.Error?.Code.ToString() ?? "INVALID_FRAME", dltResult.Package?.Error?.Message ?? "645 报文无效。");
        if (dltResult.Consumed != bytes.Length)
            return FrameValidation.Fail("MULTIPLE_OR_TRAILING_FRAMES", "下发接口一次只允许一个完整帧。");
        return FrameValidation.Ok(dltFrame);
    }

    /// <summary>
    /// 把两类协议帧投影为适合 HTTP 调试查看的结构化字段。
    /// </summary>
    private static object ToDecodeResponse(IProtocolFrame frame) => frame switch
    {
        QGdw3761Frame value => new
        {
            protocol = "376.1",
            value.DeviceId,
            value.UserDataLength,
            control = $"{value.Control:X2}",
            value.IsUpstream,
            value.IsPrimaryMessage,
            value.LinkFunctionCode,
            value.AdministrativeDivision,
            value.TerminalAddress,
            value.MasterStationAddress,
            afn = $"{value.Afn:X2}",
            value.Sequence,
            value.Points,
            value.Functions,
            value.LinkMessageKind,
            payloadHex = HexCodec.ToHex(value.DataUnitPayload),
            rawHex = HexCodec.ToHex(value.Raw)
        },
        Dlt645Frame value => new
        {
            protocol = "645-2007",
            value.DeviceId,
            value.Address,
            control = $"{value.Control:X2}",
            value.IsResponse,
            value.IsAbnormalResponse,
            value.HasMoreFrames,
            value.FunctionCode,
            dataIdentifier = value.DataIdentifier?.ToString("X8"),
            dataHex = HexCodec.ToHex(value.Data),
            rawHex = HexCodec.ToHex(value.Raw)
        },
        _ => new { frame.Protocol, frame.DeviceId, frame.Summary, rawHex = HexCodec.ToHex(frame.Raw) }
    };

    /// <summary>
    /// 统一错误响应格式，便于调用方按 code 处理并通过 traceId 关联日志。
    /// </summary>
    private static IResult ApiProblem(int statusCode, string code, string message, string? traceId = null) => Results.Problem(
        statusCode: statusCode,
        title: code,
        detail: message,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["traceId"] = traceId
        });
}
