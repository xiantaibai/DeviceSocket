using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 协议指令 Handler 的启动期自动注册扩展。
/// </summary>
public static class ProtocolHandlerRegistrationExtensions
{
    /// <summary>
    /// 扫描指定程序集中所有统一 Handler 接口实现，新增 Handler 无需再手工修改 DI 注册。
    /// </summary>
    public static IServiceCollection AddProtocolInstructionHandlers(
        this IServiceCollection services,
        Assembly assembly)
    {
        var openHandlerType = typeof(IProtocolInstructionHandler<,>);
        var registrations = assembly.DefinedTypes
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.ImplementedInterfaces
                .Where(contract => contract.IsGenericType &&
                                   contract.GetGenericTypeDefinition() == openHandlerType)
                .Select(contract => new { Implementation = type.AsType(), Contract = contract }))
            .ToArray();

        foreach (var registration in registrations)
            services.AddSingleton(registration.Contract, registration.Implementation);

        return services;
    }
}
