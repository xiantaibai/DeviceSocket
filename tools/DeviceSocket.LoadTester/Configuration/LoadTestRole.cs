namespace DeviceSocket.LoadTester.Configuration;

/// <summary>
/// 指定当前 LoadTester 进程承担设备模拟、HTTP 指令驱动或两者组合的运行角色。
/// </summary>
public enum LoadTestRole
{
    /// <summary>
    /// 只建立并维护虚拟设备 TCP 会话，不调用业务 HTTP API。
    /// </summary>
    DevicesOnly,

    /// <summary>
    /// 只调用业务 HTTP API；设备会话由其他负载节点负责。
    /// </summary>
    ApiOnly,

    /// <summary>
    /// 在同一进程内同时维护设备 TCP 会话并调用业务 HTTP API。
    /// </summary>
    Combined
}
