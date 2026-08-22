# Linux 单节点 1～5 万连接压测手册

## 1. 目标与边界

本手册用于在一台 8 核 16 GiB Linux 服务器上验证单个 `DeviceSocket.Host` Gateway 进程。目标设备周期发送 Q/GDW 376.1 心跳，Gateway 正常回复确认。

“单节点”指被测 Gateway 只有一台。负载发生器应放在其他主机上，否则 LoadTester 的连接、CPU、内存和网络开销会污染 Gateway 结果。单个源 IP 连接同一目标 IP/端口还会受到本地临时端口数量限制，30,000～50,000 连接建议使用 3～5 个负载分片，并分布到多个源 IP 或多台负载机。

代码已提供场景和服务端观测能力，但仓库本身不能替代目标 Linux 环境的实际结果。每轮报告必须保存软件版本、配置、内核参数、机器规格和异常日志。

## 2. 发布与部署

推荐直接使用仓库提供的 Docker 交付资产：

- 镜像构建和离线导出脚本：`src/Gateway/DeviceSocket.Host/build-docker-image.ps1`；
- Linux Compose、环境变量、日志挂载和 sysctl 基线：`src/Gateway/DeviceSocket.Host/deploy/linux/`；
- 完整启动、健康检查、更新和回滚步骤：`src/Gateway/DeviceSocket.Host/deploy/linux/README.md`。

Docker 容量验收使用 `network_mode: host`，负载机直接连接 Linux 宿主机的 5001/5002 端口。压测期间同时采集 Gateway 状态 API、`docker stats` 和宿主机 `/proc/net/sockstat`，不能只用进程工作集代表容器及 Socket 总内存。

不使用 Docker 时，可继续采用以下框架依赖发布方式。

在构建机执行：

```powershell
dotnet publish src\Gateway\DeviceSocket.Host\DeviceSocket.Host.csproj `
  -c Release -r linux-x64 --self-contained false `
  -o artifacts\gateway-linux-x64

dotnet publish tools\DeviceSocket.LoadTester\DeviceSocket.LoadTester.csproj `
  -c Release -r linux-x64 --self-contained false `
  -o artifacts\load-tester-linux-x64
```

目标主机和负载机安装 .NET 8 Runtime，把对应目录复制到 Linux。Gateway 目录必须包含 `appsettings.json` 和 `Configuration/Logging/serilog.json`。

首次只使用本地内存模式验证 TCP 层：

```bash
export Orleans__Enabled=false
export DeviceSocket__ConnectionLimit__MaxConnections=85000
export DeviceSocket__ConnectionLimit__MaxUnauthenticatedConnections=5000
export DeviceSocket__ConnectionLimit__MaxConnectionsPerIp=0
export DeviceSocket__ConnectionLimit__ReceiveBufferSize=4096
export DeviceSocket__ConnectionLimit__SendBufferSize=4096
dotnet DeviceSocket.Host.dll
```

日志默认写入 Gateway 内容目录下的 `logs/`。生产式压测应把三条日志路径改到容量充足的独立数据盘，并确认运行用户有写权限。心跳日志保持默认关闭：

```bash
export DeviceLogging__HeartbeatSampleRate=0
```

设备列表直接使用 `serilog.json` 的默认空数组即可。排查指定设备时再把 `DeviceLogging__HeartbeatEnabledDeviceIds__0` 设置为真实设备编号；不要把它设置为空字符串，否则启动校验会拒绝无效配置。

## 3. Linux 资源基线

先记录系统和当前限制，不要在没有证据时批量修改 TCP 参数：

```bash
uname -a
lscpu
free -h
ulimit -n
sysctl fs.file-max net.core.somaxconn net.ipv4.tcp_max_syn_backlog
```

Gateway 进程至少需要覆盖监听 Socket、50,000 个设备 Socket、HTTP/RPC、日志文件和运行时文件描述符。建议服务进程软硬限制均不低于 120,000；若仍保留 80,000 上限验证计划，可继续使用 200,000。使用 systemd 时：

```ini
[Service]
WorkingDirectory=/opt/devicesocket/gateway
ExecStart=/usr/bin/dotnet /opt/devicesocket/gateway/DeviceSocket.Host.dll
Restart=on-failure
LimitNOFILE=200000
Environment=Orleans__Enabled=false
Environment=DeviceSocket__ConnectionLimit__MaxConnections=85000
```

两个协议 Listener 的默认 `BackLog=4096` 只限制已完成 TCP 握手、等待 SuperSocket 接受的连接队列，和 50,000 条已接受长连接没有直接比例关系。Linux 会使用 `net.core.somaxconn` 作为该队列的内核上限，因此起始值保持不低于应用 `BackLog` 即可。

`net.ipv4.tcp_max_syn_backlog` 管理的是尚未完成握手的 SYN 队列，不要求和应用 `BackLog` 相等。只有 SYN 队列溢出或集中重连压测显示半连接丢弃时才独立调整。先用临时 `sysctl -w` 验证，确认有效后再写入 `/etc/sysctl.d/`。服务端不需要为了长连接测试缩短 `tcp_fin_timeout` 或开启激进的 `tcp_tw_reuse`。

压测爬升和集中重连期间执行：

```bash
ss -lnt '( sport = :5001 or sport = :5002 )'
nstat -az TcpExtListenOverflows TcpExtListenDrops
```

监听状态下 `Recv-Q` 表示当前等待接受的连接数，`Send-Q` 表示生效的队列上限。只有 `Recv-Q` 持续接近 `Send-Q`，或 `TcpExtListenOverflows`、`TcpExtListenDrops` 相对本轮开始值增长，才按 4096、8192 逐档提高应用 `BackLog` 和 `somaxconn` 后复测。

4 KiB `ReceiveBufferSize` / `SendBufferSize` 是本项目小报文长连接的起始值，不会限制协议半包重组。放大会增加 5 万连接下的 Socket 内存占用；只有看到持续吞吐受限、发送积压或大报文占比高时，才按 8 KiB、16 KiB 逐档复测。

## 4. 运行前检查

启动 Gateway 后执行：

```bash
curl -fsS http://127.0.0.1:5080/api/status/connections | jq .
ss -lntp | grep -E ':5001|:5002|:5080|:7001'
pid=$(pgrep -f 'DeviceSocket.Host.dll' | head -n 1)
cat /proc/$pid/limits | grep -i 'open files'
```

确认：

- 5001、5002、5080 和 7001 按配置监听；
- `Max open files` 不低于 200,000；
- `applicationLogDropped`、`upstreamLogDropped`、`downstreamLogDropped` 为零；
- 初始连接、发送积压和线程池排队接近零；
- 日志数据盘至少能容纳本轮程序、上行和下行日志及滚动余量。

## 5. 分阶段执行

仓库正式场景已经收敛为 Gather.TcpCenter 测试。验证当前 DeviceSocket Gateway 时，可复制 `gather-tcpcenter-soak-10000.json` 为本地 `devicesocket-soak-50000.local.json`，把 `compatibilityProfile` 改为 `deviceSocket`，按目标规模配置 sequential 设备空间，并启用 `serverObservation`。本地派生文件不要提交为新的正式场景。

先把派生场景中的 `target.host` 和 `serverObservation.statusUrl` 改成 Gateway 可达地址。正式测试使用统一 UTC 启动时间，每个分片只修改 `shardIndex`：

```bash
dotnet DeviceSocket.LoadTester.dll run \
  --config Scenarios/devicesocket-soak-50000.local.json \
  --run-id linux-50k-001 \
  --shard-index 0 \
  --shard-count 4 \
  --start-at-utc 2026-08-10T14:00:00Z
```

其他三台负载机分别使用 `shard-index` 1、2、3。建议依次执行：

| 阶段 | 连接数 | 稳态时间 | 目的 |
| --- | ---: | ---: | --- |
| 冒烟 | 10,000 | 30 分钟 | 验证登录、心跳确认、报告和日志指标 |
| 中间容量 | 30,000 | 1 小时 | 观察 CPU、内存和 Session 扫描随连接数增长趋势 |
| 目标容量 | 50,000 | 6 小时，最终扩展到 24 小时 | 验证目标容量和长期内存稳定性 |

10,000、30,000 和 50,000 阶段分别使用独立的本地派生配置，只修改 `devices.count`、场景名、稳态时长和服务端观测门槛。所有分片继续填写全局设备数和全局连接速率，LoadTester 会按分片数换算；不要把总速率重复配置到每台机器。

每个容量阶段可按风险补充以下峰值模型：

1. 把 `heartbeat.jitterSeconds` 设为 0，验证同步心跳；
2. 分批主动断开并重连，验证接入限速、在线恢复和路由替换；
3. 注入黑洞或慢接收，验证 KeepAlive、合法活动超时、发送超时和背压关闭；
4. 保持业务日志开启，确认心跳默认过滤不会掩盖业务通信日志。

## 6. 采集命令

压测期间每 5～10 秒采样 Gateway 状态 API。LoadTester 已自动记录主要指标；主机侧同时保留下列信息：

```bash
pid=$(pgrep -f 'DeviceSocket.Host.dll' | head -n 1)
pidstat -p "$pid" 5
vmstat 5
ss -s
cat /proc/net/sockstat
ls /proc/$pid/fd | wc -l
du -sh /opt/devicesocket/gateway/logs
```

重点观察：

- `tcpConnections`、`authenticatedDevices`、`peakTcpConnections`；
- `processCpuPercent`、`processWorkingSetBytes`、`managedHeapBytes`、`gcPauseTimePercentage`；
- `threadPoolPendingWorkItems` 和可用 Worker/I/O 线程；
- 5001、5002 监听端口的 `Recv-Q` / `Send-Q` 和 `TcpExtListenOverflows`、`TcpExtListenDrops` 增量；
- `pendingSend*`、`peakPendingSend*`、发送超时与背压关闭；
- `maximumSessionScanDurationMilliseconds`，以及候选数与实际关闭数；
- 三条日志缓冲水位、累计丢弃和同步提交失败；
- 心跳接收数与确认数的增长趋势。

## 7. 验收口径

一轮容量测试至少满足：

- 最终在线率和 Gateway 新增认证设备峰值均不低于目标的 99%；
- 稳态期间无进程崩溃、OOM、持续增长且不回落的工作集/托管堆；
- 程序、上行、下行日志新增丢弃数均为零；
- 无持续增长的线程池排队、发送积压和 Socket orphan；
- 协议错误率不高于场景配置阈值，心跳确认无持续超时；
- Session 扫描耗时明显小于 30 秒扫描周期，并且不会误关刚登录或刚收到合法心跳的连接；
- CPU 留有故障恢复余量。建议稳态平均值不长期超过约 70%，短时峰值需结合持续时间和线程池排队判断；
- 日志磁盘写入、文件描述符和 Socket 内存没有逼近主机上限。

若 50,000 连接未通过，不直接扩大接收缓冲或异步队列。先根据状态 API、`pidstat`、`sockstat` 和日志丢弃判断瓶颈属于 CPU、GC、Socket 内存、文件描述符、磁盘还是负载机端口，再只调整对应参数并复测。

## 8. 报告归档

收集四个分片目录后执行：

```bash
dotnet DeviceSocket.LoadTester.dll report merge \
  --input collected/linux-50k-001 \
  --output collected/linux-50k-001/aggregate
```

归档内容至少包括：

- `node-timeseries.jsonl`、各节点 `node-summary.json`、`report.md` 和汇总报告；
- Gateway 发布版本或提交标识、`appsettings.json`、`serilog.json`；
- `/proc/$pid/limits`、相关 `sysctl`、机器规格和内核版本；
- 程序告警日志、三条日志文件的大小与丢弃指标；
- 结论、瓶颈、参数调整以及下一轮复测计划。
