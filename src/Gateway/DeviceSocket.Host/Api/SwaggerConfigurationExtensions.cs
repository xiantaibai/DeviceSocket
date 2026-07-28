using Microsoft.OpenApi;

namespace DeviceSocket.Host.Api;

/// <summary>
/// 集中维护 DeviceSocket OpenAPI 文档和 Swagger UI 配置。
/// </summary>
public static class SwaggerConfigurationExtensions
{
    /// <summary>
    /// 注册 Minimal API 发现服务和 Swagger 文档生成器。
    /// </summary>
    /// <param name="services">应用服务集合。</param>
    /// <returns>用于继续链式注册的原服务集合。</returns>
    public static IServiceCollection AddDeviceSwagger(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "DeviceSocket API",
                Version = "v1",
                Description = "Q/GDW 376.1-2012 与 DL/T 645-2007 设备接入、状态查询、协议解析和消息下发接口。"
            });
        });

        return services;
    }

    /// <summary>
    /// 启用 OpenAPI JSON 和可交互的 Swagger UI 页面。
    /// </summary>
    /// <param name="app">当前 Web 应用。</param>
    /// <returns>用于继续配置中间件的原应用。</returns>
    public static WebApplication UseDeviceSwagger(this WebApplication app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "DeviceSocket API v1");
            options.DocumentTitle = "DeviceSocket API";
            options.DisplayRequestDuration();
            options.EnableTryItOutByDefault();
        });

        return app;
    }
}
