@{
    # 证据统一写到该目录；可在启动命令中用 -OutputRoot 临时覆盖。
    OutputRoot = 'D:\GatherEvidence'

    # 五万设备场景默认预留两小时；可按 Q 提前正常结束。
    DurationSeconds = 7200

    # 长时间压测每 5 秒记录一次即可，避免监控脚本自身制造明显开销。
    IntervalSeconds = 5

    # 目标只允许三种精确定位方式：
    # Service：Windows 服务内部 Name，可跟随服务重启后的新 PID。
    # Port：本机监听端口，适合 ConnectService 或独立 API，可跟随监听进程变化。
    # ProcessId：固定 PID，不跟随重启，只适合本轮已经核实的临时进程。
    # 首次部署只修改本文件；后续每轮只需要填写 RunId。
    Targets = @(
        @{
            Name = 'Connect-8890'
            Type = 'Port'
            Value = 8890
            Enabled = $true
        }

        # 将 Value 改成测试服务器的 Windows 服务内部名称后再启用。
        @{
            Name = 'DirectiveService'
            Type = 'Service'
            Value = '请填写DirectiveService内部名称'
            Enabled = $false
        }
        @{
            Name = 'TimerTaskService'
            Type = 'Service'
            Value = '请填写TimerTaskService内部名称'
            Enabled = $false
        }

        # IssueWebApi 如果是独立监听进程，推荐改成 Port；如果是 Windows 服务则改成 Service。
        @{
            Name = 'IssueWebApi'
            Type = 'Port'
            Value = 5000
            Enabled = $false
        }
    )
}
