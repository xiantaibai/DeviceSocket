# 协议消息处理架构

## 处理链路

```text
SuperSocket ServerWorker
  -> ProtocolMessagePipeline<TFrame>
     -> 合法性校验、MsgId、指标、流量日志、异常隔离
     -> IProtocolMessageDispatcher<TFrame>
        -> 协议连接行为
        -> ProtocolInstructionDispatcher<TFrame, TInstructionKey>
           -> IProtocolInstructionResolver<TFrame, TInstructionKey>
           -> IProtocolInstructionHandler<TFrame, TInstructionKey>
```

`QGdw3761ServerWorker` 和 `Dlt645ServerWorker` 在编译期就已确定帧类型，因此不再把两种协议放回同一个处理器中做协议 `switch`。

## 统一 Handler 接口

所有协议指令 Handler 统一实现：

```csharp
IProtocolInstructionHandler<TFrame, TInstructionKey>
```

- `TFrame` 保留协议帧的强类型字段。
- `TInstructionKey` 保留协议指令键的强类型规则。
- 376.1 使用链路命令类型或 `AFN + Fn`。
- 645 使用功能码、数据标识和应答方向。

Gateway 启动时扫描当前程序集，自动注册所有该接口的具体实现。新增指令只需新增 Handler 类，不再修改 `Program.cs`、ServerWorker 或 DI 扩展。

## 分发与异常规则

- 程序启动时将 Handler 构建为只读字典，收包时不执行反射或线性遍历。
- 相同指令键存在多个 Handler 时启动失败，避免运行时随机选择。
- 每种协议必须注册一个 Fallback Handler。
- 未识别指令进入 Fallback Handler，保留日志和连接，不直接断开设备。
- Handler 异常由通用 Pipeline 转换为结构化错误，不向 SuperSocket 收包循环传播。
- 通用 Pipeline 统一记录原始报文，各 Handler 不重复记录整帧。

## 新增指令

1. 在对应协议的 `Processing/Handlers` 目录中新建类。
2. 实现 `IProtocolInstructionHandler<TFrame, TInstructionKey>`。
3. 通过 `Key` 声明链路命令、AFN/Fn 或 645 指令。
4. 在 `HandleAsync` 中完成协议级响应，或转换为标准业务事件后交给上行 Publisher。

不需要再编辑集中的 `case` 或手工添加 Handler DI 注册。
