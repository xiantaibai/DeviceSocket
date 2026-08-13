# DeviceSocket.Host Linux Docker 部署

本目录用于在原生 Linux Docker Engine 上以单 Gateway、host 网络模式部署 `DeviceSocket.Host`。默认目标为 8 核 16 GiB、60,000～80,000 条设备长连接；它不包含 Orleans Silo、数据库或负载发生器。

## 1. 构建或导入镜像

脚本使用带 BOM 的 UTF-8 编码，支持 Windows PowerShell 5.1 和 PowerShell 7。在仓库根目录执行：

```powershell
.\src\Gateway\DeviceSocket.Host\build-docker-image.ps1 `
  -Tag 1.0.0 `
  -Pull `
  -ArchivePath artifacts\docker\devicesocket-host-1.0.0.tar
```

如果当前目录已经是 `src\Gateway\DeviceSocket.Host`，则把第一行改为 `.\build-docker-image.ps1`。`ArchivePath` 的相对路径始终按仓库根目录解析。

脚本先把 Host 发布到 `bin/Release/net8.0/publish`，再以 Host 项目目录作为 Docker 上下文，生成 `devicesocket-host:1.0.0`，并可选导出离线 tar。脚本不会启动容器。将 tar 上传到服务器 `/data/images` 后执行：

```bash
docker load --input /data/images/devicesocket-host-1.0.0.tar
docker image inspect devicesocket-host:1.0.0 --format '{{.Id}}'
```

使用私有仓库时可跳过 tar，在构建机完成 `docker tag`、`docker push`，然后把 `.env` 中的 `DEVICE_SOCKET_IMAGE` 改成私有仓库完整地址。

## 2. 服务器目录规划

Compose 文件固定放在 `/data/dockerfile`，应用配置、镜像包和日志分别使用独立目录：

```text
/data/
├── config/
│   └── devicesocket/
│       ├── appsettings.json
│       └── serilog.json
├── docker/                         # 可供 Docker Engine data-root 使用，Compose 不直接挂载
├── dockerfile/
│   ├── .env
│   └── docker-compose.yml
├── images/
│   └── devicesocket-host-1.0.0.tar
└── logs/
    └── devicesocket/
```

`.env` 只管理镜像、宿主机目录、容器内存和时区。TCP 监听、`BackLog`、连接容量、超时、Gateway 和 Orleans 参数统一放在 `appsettings.json`，避免同一参数被环境变量高优先级覆盖。

创建目录并设置权限：

```bash
sudo install --directory --owner=root --group=root --mode=0755 /data/dockerfile
sudo install --directory --owner=root --group=root --mode=0755 /data/config/devicesocket
sudo install --directory --owner=root --group=root --mode=0755 /data/images
sudo install --directory --owner=1654 --group=1654 --mode=0750 /data/logs/devicesocket
```

配置文件由 root 管理且只读挂载，日志目录只授权给镜像内置的 `app` 用户（UID/GID 1654）。不要把目录临时改成 `0777`；如使用 NFS、NAS、SELinux 或 AppArmor，还需确认 UID 映射和安全策略允许写入日志目录。

把部署文件复制到对应位置：

```bash
sudo cp docker-compose.yml /data/dockerfile/docker-compose.yml
sudo cp .env.example /data/dockerfile/.env
sudo cp config/appsettings.json /data/config/devicesocket/appsettings.json
sudo cp config/serilog.json /data/config/devicesocket/serilog.json
sudo chown root:root /data/dockerfile/docker-compose.yml /data/dockerfile/.env \
  /data/config/devicesocket/appsettings.json /data/config/devicesocket/serilog.json
sudo chmod 0644 /data/dockerfile/docker-compose.yml \
  /data/config/devicesocket/appsettings.json /data/config/devicesocket/serilog.json
sudo chmod 0600 /data/dockerfile/.env
```

## 3. 准备 Linux 主机

确认使用 Docker Engine 和 Compose v2：

```bash
docker version
docker compose version
```

应用宿主机文件描述符和监听队列基线：

```bash
sudo cp 99-devicesocket.conf /etc/sysctl.d/99-devicesocket.conf
sudo sysctl --system
sysctl fs.file-max net.core.somaxconn net.ipv4.tcp_max_syn_backlog
```

## 4. 配置和启动

修改服务器上的部署和应用配置，然后从 Compose 目录启动：

```bash
vi /data/dockerfile/.env
vi /data/config/devicesocket/appsettings.json
vi /data/config/devicesocket/serilog.json
cd /data/dockerfile
docker compose config
docker compose up --detach
```

`docker compose config` 必须显示以下三个 bind source，且不能出现未定义 Volume 或变量警告：

```text
/data/config/devicesocket/appsettings.json
/data/config/devicesocket/serilog.json
/data/logs/devicesocket
```

Compose 对这三个路径均设置了 `create_host_path: false`。文件或目录缺失时会直接启动失败，不会自动创建 root 所有者目录。

`network_mode: host` 直接占用宿主机端口，不配置 `ports`：

| 端口 | 用途 | 暴露建议 |
| ---: | --- | --- |
| 5001/TCP | Q/GDW 376.1 设备接入 | 按设备网络开放 |
| 5002/TCP | DL/T 645 设备接入 | 按设备网络开放 |
| 5080/TCP | HTTP 状态和调试 API | 只允许管理网和压测机 |
| 7001/TCP | Gateway MagicOnion RPC | 只允许内部服务网 |

当前 HTTP、Swagger 和内部 RPC 尚未完成生产鉴权与 TLS，禁止把 5080、7001 直接暴露到公网。

## 5. 应用与日志配置

`appsettings.json` 管理以下应用参数：

- Q/GDW 376.1 和 DL/T 645 的监听 IP、端口、`BackLog` 与 `NoDelay`；
- 最大连接数、接入速率、登录和空闲超时；
- 收发缓冲、发送背压和 TCP KeepAlive；
- Gateway 节点身份、RPC 地址和 Orleans 开关。

修改 `appsettings.json` 后执行 `docker compose up --detach --no-deps gateway` 重建容器，使 TCP 子宿主使用新配置。跨主机启用 Orleans 前，必须把 `Gateway:RpcEndpoint` 从 `127.0.0.1` 改成其他服务可访问的内网地址，并配置真实集群发现。

`/data/config/devicesocket/serilog.json` 以只读方式挂载到容器，但可以在宿主机修改。Gateway 会自动重新加载 `DeviceLogging` 配置。

默认心跳不写通信日志：

```json
"HeartbeatSampleRate": 0,
"HeartbeatEnabledDeviceIds": []
```

排查单设备时把设备编号加入 `HeartbeatEnabledDeviceIds`。修改后观察状态接口中的 `heartbeatLogRecordedByDevice`；业务上报日志不会因心跳策略被过滤。

## 6. 检查运行状态

```bash
docker compose ps
curl --fail http://127.0.0.1:5080/api/status/connections
docker stats devicesocket-host
docker compose logs --tail 100 gateway
```

同时确认容器文件描述符限制：

```bash
docker exec devicesocket-host sh -c 'ulimit -n'
pid=$(docker inspect --format '{{.State.Pid}}' devicesocket-host)
sudo ls "/proc/$pid/fd" | wc -l
```

重点观察状态接口的 `tcpConnections`、`processWorkingSetBytes`、`pendingSendBytes`、`maximumSessionScanDurationMilliseconds` 和三条日志丢弃数。`docker stats` 包含 cgroup 视角的容器资源，不能只看进程工作集。

## 7. 更新和回滚

先把新离线包放入 `/data/images` 并加载，或从私有仓库拉取镜像，再修改 `/data/dockerfile/.env` 的 `DEVICE_SOCKET_IMAGE`：

```bash
# 私有仓库方式
docker compose pull gateway

# 离线包方式使用 docker load --input /data/images/devicesocket-host-<version>.tar
docker compose up --detach --no-deps gateway
docker compose ps
```

单节点更新会断开全部设备连接，应安排维护窗口并确认设备具备指数退避重连。回滚时把镜像标签改回上一个已验收版本后再次执行 `docker compose up --detach --no-deps gateway`。

停止服务：

```bash
docker compose down --timeout 30
```

`down` 不会删除宿主机 `/data/logs/devicesocket`。不要使用带 `--volumes` 的清理命令处理未知 Compose 项目。
