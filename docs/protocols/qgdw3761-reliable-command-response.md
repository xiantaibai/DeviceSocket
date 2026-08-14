# Q/GDW 376.1 可靠命令与终端应答约定

## 适用范围

本约定用于 `DeviceSocket.Host` 对 Q/GDW 376.1-2012 设备执行“每台设备一问一答”的可靠下发。当前闭环支持终端以 `AFN=00H、F3` 逐数据单元确认返回命令处理结果；遥测上报、事件主动上报和业务数据响应仍按各自 AFN/Fn Handler 处理，不能冒充命令确认。

协议依据：Q/GDW 376.1-2012 的链路用户数据、应用层功能码、SEQ（TpV/FIR/FIN/CON/PSEQ 或 RSEQ）定义，以及 `AFN=00H` 确认/否认报文中的 `F3` 逐数据单元确认。ERR 的具体含义遵循标准附录 D；Gateway 只把 `ERR=00H` 解释为成功，把非零值解释为失败并保留原始值。

## MsgId 与 PSEQ 的职责

`MsgId` 是平台生成的全局业务标识，跨 Control API、Orleans、Gateway、日志和查询库保持不变，但不会写入 376.1 报文。终端不需要理解 MsgId。

PSEQ 是当前 TCP Session 内的 4 位协议事务序号。Gateway 在可靠报文真正写入 Socket 前统一完成以下处理：

1. 校验报文是合法、完整、地址与 DeviceId 一致的主站下行启动帧，即 `DIR=0、PRM=1`。
2. 保留 SEQ 的 TpV、FIR、FIN，强制置 `CON=1`。
3. 为新 MsgId 分配 `PSEQ=0..15`；同一 MsgId 的所有正式重试复用第一次分配的 PSEQ。
4. 修改 SEQ 后重新计算 CS。调用方传入 Payload 中原有的低 4 位序号不会直接上网。

一个设备只有一条可靠命令占用应答通道，因此 PSEQ 回绕时不会存在 16 条并行事务的歧义。Session 重连后旧 PSEQ 关联立即删除，新连接重新开始分配。

## 终端必须返回的报文

终端收到 `CON=1` 的下行命令并完成处理后，应返回一条从动上行确认：

| 字段 | 约定 |
| --- | --- |
| 控制域 C | `DIR=1、PRM=0`，当前实现功能码为 `0` |
| AFN | `00H` |
| SEQ | `FIR=1、FIN=1、CON=0`，低 4 位 RSEQ 等于请求 PSEQ |
| 确认数据单元 | `DA=0000H、DT=0004H`，即测量点 0、F3 |
| 数据区第 1 字节 | 被确认请求的 AFN |
| 数据区第 2～3 字节 | 被确认请求的 DA1、DA2 |
| 数据区第 4～5 字节 | 被确认请求的 DT1、DT2 |
| 数据区第 6 字节 | ERR；`00H` 成功，非零失败 |

响应数据区可以概括为：

```text
OriginalAFN | OriginalDA1 | OriginalDA2 | OriginalDT1 | OriginalDT2 | ERR
```

完整示意：

```text
Gateway                                            Terminal
DIR=0 PRM=1 AFN=x CON=1 PSEQ=n DA/DT=y  ──────────> 校验、识别、执行
                                                   │
DIR=1 PRM=0 AFN=00 F3 RSEQ=n [x,y,ERR]   <──────────┘
```

Gateway 只有在以下字段同时匹配时，才恢复原 `MsgId + AttemptNo + SessionEpoch` 并调用 `IDeviceGrain.ReceiveCommandReplyAsync`：

- TCP Session 和设备地址；
- RSEQ 与当前命令 PSEQ；
- 回显的原 AFN、DA1、DA2、DT1、DT2；
- 报文到达时间没有超过当前正式尝试的回复截止时间；
- 命令没有超过整体 ExpiresAt。

仅匹配 RSEQ 不够，因为 PSEQ 只有 4 位；仅匹配设备编号也不够，因为旧回复可能在下一条命令期间迟到。

## 终端重试幂等要求

平台可能因为未收到确认、Gateway 写入失败或 RPC 结果不确定而重发同一逻辑命令。终端必须把“同一主站地址、同一有效 Session、相同 PSEQ、AFN、DA、DT”视为同一事务：

1. 首次收到时执行业务操作并缓存处理结果。
2. 再次收到相同事务时不得重复执行有副作用的动作，应直接重发缓存的 F3 确认。
3. 即使首次确认可能已经发出，只要收到重复请求仍应再次发送同一结果。
4. 收到新的 PSEQ 或新的 AFN/DA/DT 后，才作为新事务处理。
5. 终端重启导致缓存丢失时，业务动作自身仍应使用业务流水号、参数版本或目标状态实现幂等；PSEQ 只能解决单 Session 的短期重试，不能替代业务幂等键。

建议终端至少缓存最近 16 个事务结果，或缓存时间覆盖平台命令最大有效期。写参数、合分闸、升级等有副作用命令必须先持久化事务结果，再发送成功确认，避免终端重启后重复执行。

## 超时、迟到和重复响应

平台区分两个时间边界：

- `ResponseTimeout`：一次正式写入完成后等待设备回复的时长。超过后，该次响应窗口关闭并进入退避/重试判断。
- `ExpiresAt`：整条逻辑命令的最终到期时间。到期后停止重试并删除 Gateway 关联。

处理规则：

- 一直没有设备回复：Orleans 到 ResponseTimeout 后按 MaxAttempts 和退避策略重试；耗尽后标记 `Failed`，整体先到期则标记 `Expired`。
- Gateway 报告写入失败但终端实际收到完整报文：这属于部分写入的不确定边界；失败结果只合并当次并发调用，Orleans 保持同一 AttemptNo，Gateway 使用同一 PSEQ 安全重发；终端必须返回缓存结果而不是重复执行。
- 很久才收到回复：到达或超过当前 ReplyDeadline 的 F3 报文直接丢弃，不调用 Orleans、不改变命令状态；等待下一正式重试时继续复用原 PSEQ。
- 重复回复：同一次回复已经在提交或已经确认后，后续重复报文直接丢弃。
- 错 Session、错 PSEQ、错 AFN/DA/DT：直接丢弃，不能确认当前命令。
- Session 断开或被重复登录替换：立即清除该 Session 的关联；旧连接迟到回复不处理。
- Orleans 临时不可用：Gateway 不把回复标记为已消费，终端重复确认或下一次正式重试仍可再次提交。

Gateway 每个 Session 最多保留一条关联，单节点默认上限 100000 条；整体到期清理由一个节点级周期任务完成，不为每台设备创建 Timer。

## Gateway 配置

配置均使用整数，避免 TimeSpan 字符串中的小数和天数格式歧义：

```jsonc
{
  "DeviceSocket": {
    "Protocols": {
      "QGdw3761": {
        "CommandCorrelation": {
          "MaxPendingCommands": 100000,
          "CleanupIntervalSeconds": 30
        }
      }
    }
  }
}
```

`ResponseTimeout`、`ExpiresAt` 和 MaxAttempts 属于每条 Orleans 命令策略，不在 Gateway 重复配置。Gateway 只使用 Silo 随下发请求传来的绝对到期时间和回复时长进行协议关联。
