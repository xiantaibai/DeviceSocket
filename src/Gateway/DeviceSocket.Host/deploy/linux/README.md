# DeviceSocket.Host Linux Docker 部署

本目录用于在原生 Linux Docker Engine 上以单 Gateway、host 网络模式部署 `DeviceSocket.Host`。默认目标为 8 核 16 GiB、60,000～80,000 条设备长连接；它不包含 Orleans Silo、数据库或负载发生器。

## 1. 构建或导入镜像

在仓库根目录或 Host 项目目录执行 PowerShell 7：

```powershell
src/Gateway/DeviceSocket.Host/build-docker-image.ps1 `
  -Tag 1.0.0 `
  -Pull `
  -ArchivePath artifacts/docker/devicesocket-host-1.0.0.tar
```

脚本先把 Host 发布到 `bin/Release/net8.0/publish`，再以 Host 项目目录作为 Docker 上下文，生成 `devicesocket-host:1.0.0`，并可选导出离线 tar。脚本不会启动容器。把 tar 和本目录复制到 Linux 后执行：

```bash
docker load --input devicesocket-host-1.0.0.tar
docker image inspect devicesocket-host:1.0.0 --format '{{.Id}}'
```

使用私有仓库时可跳过 tar，在构建机完成 `docker tag`、`docker push`，然后把 `.env` 中的 `DEVICE_SOCKET_IMAGE` 改成私有仓库完整地址。

## 2. 准备 Linux 主机

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

创建日志目录。`.NET 8` 官方镜像内置的非 root 用户 `app` 使用 UID 1654：

```bash
sudo install --directory --owner=1654 --group=1654 --mode=0750 /data/devicesocket/logs
```

不要把目录临时改成 `0777`。如使用 NFS、NAS 或安全模块，还需要确认挂载选项、SELinux/AppArmor 和 UID 映射允许 UID 1654 写入。

## 3. 配置和启动

在本目录创建部署配置：

```bash
cp .env.example .env
vi .env
vi config/serilog.json
docker compose config
docker compose up --detach
```

`network_mode: host` 直接占用宿主机端口，不配置 `ports`：

| 端口 | 用途 | 暴露建议 |
| ---: | --- | --- |
| 5001/TCP | Q/GDW 376.1 设备接入 | 按设备网络开放 |
| 5002/TCP | DL/T 645 设备接入 | 按设备网络开放 |
| 5080/TCP | HTTP 状态和调试 API | 只允许管理网和压测机 |
| 7001/TCP | Gateway MagicOnion RPC | 只允许内部服务网 |

当前 HTTP、Swagger 和内部 RPC 尚未完成生产鉴权与 TLS，禁止把 5080、7001 直接暴露到公网。

## 4. 日志配置热更新

`config/serilog.json` 以只读方式挂载到容器，但可以在宿主机修改。Gateway 会自动重新加载 `DeviceLogging` 配置。

默认心跳不写通信日志：

```json
"HeartbeatSampleRate": 0,
"HeartbeatEnabledDeviceIds": []
```

排查单设备时把设备编号加入 `HeartbeatEnabledDeviceIds`。修改后观察状态接口中的 `heartbeatLogRecordedByDevice`；业务上报日志不会因心跳策略被过滤。

## 5. 检查运行状态

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

## 6. 更新和回滚

先加载或拉取新版本镜像，再修改 `.env` 的 `DEVICE_SOCKET_IMAGE`：

```bash
# 私有仓库方式
docker compose pull gateway

# 离线包方式使用 docker load --input devicesocket-host-<version>.tar
docker compose up --detach --no-deps gateway
docker compose ps
```

单节点更新会断开全部设备连接，应安排维护窗口并确认设备具备指数退避重连。回滚时把镜像标签改回上一个已验收版本后再次执行 `docker compose up --detach --no-deps gateway`。

停止服务：

```bash
docker compose down --timeout 30
```

`down` 不会删除宿主机 `/data/devicesocket/logs`。不要使用带 `--volumes` 的清理命令处理未知 Compose 项目。
