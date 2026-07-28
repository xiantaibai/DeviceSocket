# 设备集群与内部 RPC 架构

## 服务边界

```text
设备
  │ 376.1 / 645 TCP
  ▼
Gateway ──Orleans Client──> Cluster Silo <──Orleans Client── 业务服务
  ▲                              │
  └────── MagicOnion Unary ──────┘
```

- Gateway 保存真实 SuperSocket Session，只负责连接、协议解析、日志和原始报文收发。
- Cluster Silo 保存可序列化的设备路由、网关租约、SessionEpoch 和可靠消息状态。
- 业务服务直接通过 Orleans Client 调用 `IDeviceGrain`，不再经过独立 CommandCenter 和额外 HTTP 调用。
- `IDeviceGrain` 是设备消息统一入口；`IDeviceCommandGrain` 按 MsgId 独立管理可靠消息的持久化、重试、到期和确认。
- MagicOnion 使用 Unary 请求响应，不使用 StreamingHub 承担关键下发。

## 登录与路由

1. Gateway 在本地内存注册设备 Session。
2. 376.1 登录确认立即在本地发送，不等待 Orleans。
3. 登录事件进入 65536 容量的有界路由队列。
4. 后台 Worker 调用 `IDeviceGrain.RegisterConnectionAsync`。
5. Grain 在新 Session 登录时递增 `SessionEpoch`。
6. Gateway 只在 Session 仍匹配时把 Epoch 写回本地连接快照。
7. 心跳刷新设备路由租约；断开事件按 GatewayNodeId 和 SessionId 条件失效路由。

上述条件更新用于解决“旧连接关闭事件晚于设备重连到达”的竞态，旧事件不能删除或覆盖新 Session。

## 下发链路

### 普通消息

1. 业务服务取得目标 `IDeviceGrain`。
2. 调用 `SendBestEffortMessageAsync`。
3. DeviceGrain 查询设备路由和网关节点租约。
4. Silo 根据网关 `RpcEndpoint` 复用 MagicOnion gRPC Channel。
5. Gateway 校验 DeviceId、Protocol、SessionId 和 SessionEpoch，然后写入 Socket。
6. 离线、路由失效或调用失败直接返回，不持久化、不自动重试。

### 重要消息

1. 业务服务生成全局唯一 `MsgId`，调用 `SendReliableMessageAsync`。
2. DeviceGrain 取得以 MsgId 为主键的 `IDeviceCommandGrain`。
3. CommandGrain 先把报文、到期时间和状态持久化到 `CommandStore`。
4. DeviceGrain 选择当前 Gateway，并通过 MagicOnion 执行首次下发。
5. Gateway 使用 `MsgId + AttemptNo` 去重，然后校验当前 Session 并写入 Socket。
6. CommandGrain 根据结果进入 `Completed`、`AwaitingConfirmation`、`RetryPending`、`Dispatching` 或 `Failed`。
7. Orleans Reminder 在默认五分钟到期前调度后续重试；到期后进入 `Expired`。

`Completed` 不是设备确认。对必须收到确认的消息，需要针对具体 AFN（应用层功能码）、功能码和 645 控制码建立响应匹配规则，再调用 `IDeviceCommandGrain.ConfirmAsync` 更新为 `Confirmed`。

Reminder 调度 DeviceGrain 时使用 Orleans 单向消息，DeviceGrain 完成 Gateway 调用后再把结果写回 CommandGrain。这样可以避免业务调用与后台重试同时发生时，两个 Grain 互相等待形成调用环。

## 异常语义

- Orleans 暂时失败：路由事件最多重试三次，Socket 消息处理不因远程异常崩溃。
- Gateway RPC 结果不确定：CommandGrain 保持相同 AttemptNo 重试，Gateway 可通过 `MsgId + AttemptNo` 返回已处理结果。
- Gateway 明确未写入：CommandGrain 增加 AttemptNo 后重试，避免把一次确定失败永久缓存。
- 设备重连：旧 SessionId 或 SessionEpoch 会被 Gateway 拒绝为 `StaleSession`。
- 网关重启：InstanceId 改变，旧节点租约过期；设备重连后建立新路由。
- 重复 MsgId：内容一致时返回原状态；内容冲突时拒绝，不能用一个 MsgId 表示两条消息。
- 日志磁盘异常：只记录错误，不抛回协议处理线程；有界队列防止日志积压无限占用内存。

## 当前存储与生产替换点

当前 Silo 使用 `DeviceStore`、`GatewayStore`、`CommandStore` 和内存 Reminder，仅用于本机开发。生产环境至少需要替换：

- Orleans 集群发现。
- Grain 持久化存储。
- 可靠消息与 Reminder 持久化、历史指令归档和 Outbox。
- Kafka 上报事件与失败重放。
- 内部 MagicOnion 的 mTLS 或服务身份认证。

契约项目保持不变，替换工作应集中在 Silo 配置、Infrastructure 和部署配置中。
