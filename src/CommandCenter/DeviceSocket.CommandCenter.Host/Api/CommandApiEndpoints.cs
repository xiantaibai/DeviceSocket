using DeviceSocket.CommandCenter.Host.Api.Models;
using DeviceSocket.CommandCenter.Host.Commands;
using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Core.Protocols.Dlt645;
using DeviceSocket.Core.Protocols.QGdw3761;

namespace DeviceSocket.CommandCenter.Host.Api;

/// <summary>
/// 统一维护 CommandCenter 的 Swagger HTTP API。
/// </summary>
public static class CommandApiEndpoints
{
    /// <summary>
    /// 注册设备指令创建端点。
    /// </summary>
    public static IEndpointRouteBuilder MapCommandApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Ok(new
        {
            service = "DeviceSocket CommandCenter",
            runtime = ".NET 8",
            swagger = "/swagger"
        }))
            .WithTags("服务")
            .WithSummary("获取 CommandCenter 服务信息");

        endpoints.MapPost("/api/commands", SendCommandAsync)
            .WithName("CreateDeviceCommand")
            .WithTags("指令")
            .WithSummary("通过 Orleans 路由和 MagicOnion 向指定设备下发报文")
            .Accepts<CreateCommandRequest>("application/json")
            .Produces<CommandDispatchResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return endpoints;
    }

    private static async Task<IResult> SendCommandAsync(
        CreateCommandRequest request,
        HttpContext context,
        CommandDispatchService dispatcher)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return Results.Problem(statusCode: 400, title: "INVALID_DEVICE_ID", detail: "deviceId 不能为空。");
        if (string.IsNullOrWhiteSpace(request.Protocol))
            return Results.Problem(statusCode: 400, title: "INVALID_PROTOCOL", detail: "protocol 不能为空。");
        if (!HexCodec.TryParse(request.Hex, out var payload, out var error))
            return Results.Problem(statusCode: 400, title: "INVALID_HEX", detail: error);
        if (!TryNormalizeProtocol(request.Protocol, out var protocol))
            return Results.Problem(statusCode: 400, title: "INVALID_PROTOCOL", detail: "protocol 仅支持 376.1 或 645。");
        if (!TryValidateFrame(protocol, payload, request.DeviceId, out var frameError))
            return Results.Problem(statusCode: 400, title: "INVALID_FRAME", detail: frameError);

        var result = await dispatcher.DispatchAsync(
            request.DeviceId,
            protocol.ToString(),
            payload,
            request.MsgId,
            context.TraceIdentifier);
        return Results.Ok(result);
    }

    /// <summary>
    /// 把调试人员常用的协议别名转换为内部稳定枚举名称。
    /// </summary>
    private static bool TryNormalizeProtocol(string value, out DeviceProtocol protocol)
    {
        if (value.Equals("376.1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("3761", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("QGdw3761", StringComparison.OrdinalIgnoreCase))
        {
            protocol = DeviceProtocol.QGdw3761;
            return true;
        }

        if (value.Equals("645", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("645-2007", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Dlt6452007", StringComparison.OrdinalIgnoreCase))
        {
            protocol = DeviceProtocol.Dlt6452007;
            return true;
        }

        protocol = default;
        return false;
    }

    /// <summary>
    /// 在指令建档前校验报文完整性、校验和及帧内设备地址，拒绝把错误数据交给网关。
    /// </summary>
    private static bool TryValidateFrame(DeviceProtocol protocol, byte[] payload, string deviceId, out string? error)
    {
        IProtocolFrame? frame;
        int consumed;
        if (protocol == DeviceProtocol.QGdw3761)
        {
            var result = QGdw3761Codec.Read(payload);
            frame = result.Package;
            consumed = result.Consumed;
        }
        else
        {
            var result = Dlt645Codec.Read(payload);
            frame = result.Package;
            consumed = result.Consumed;
        }

        if (frame is not { IsValid: true })
        {
            error = frame?.Error?.Message ?? "报文不完整或格式不合法。";
            return false;
        }

        if (consumed != payload.Length)
        {
            error = "一次下发只允许包含一条完整报文。";
            return false;
        }

        if (!string.Equals(frame.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            error = $"帧内设备地址 {frame.DeviceId} 与目标设备 {deviceId} 不一致。";
            return false;
        }

        error = null;
        return true;
    }
}
