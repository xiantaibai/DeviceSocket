# DeviceSocket 项目开发规则

## 适用范围

本规则适用于当前仓库内的全部源码、测试、脚本和说明文档。

## 中文注释要求

1. 新增或修改的类、接口、记录类型、枚举、构造函数及所有方法（包括公共、受保护、内部和私有方法），必须添加准确的中文 XML 注释（`/// <summary>`）；有参数、返回值或明确失败语义时必须同步补充 `param`、`returns`、`exception` 等说明。
2. 协议编解码、长度计算、校验和、字节序、状态切换、Session 替换和异常恢复等不易直接理解的逻辑，必须补充中文行内注释，说明“为什么这样处理”以及对应协议规则。
3. 接口方法的注释应说明行为、关键参数、返回值和失败语义；实现类不需要机械重复接口注释，但存在额外约束时必须注明。
4. 修改既有行为时必须同步更新相关中文注释，禁止保留与实际代码不一致的过期说明。
5. 注释应描述业务意图、协议依据和边界条件，避免仅把代码逐字翻译成中文。
6. 测试方法应通过中文注释或清晰的测试名称说明验证场景，特别是半包、粘包、非法校验和和断线重连等异常路径。
7. 源文件统一使用 UTF-8 编码；协议名、字段名和缩写首次出现时保留标准名称，例如 `AFN（应用层功能码）`。
8. 新增或修改的强类型配置类及其每个配置属性必须添加中文 XML 注释，说明配置路径、单位、默认值、零值含义和关键影响；不得只写“获取或设置某值”之类无业务信息的注释。
9. 新增或修改 `appsettings*.json`、YAML、环境变量示例和部署参数时，配置格式支持注释的必须在配置项附近添加中文注释；格式不支持注释的必须在对应 Options 属性和相关说明文档中补充中文说明，禁止为添加注释而破坏标准格式或部署工具兼容性。
10. 配置项名称、默认值或行为发生变化时，必须同步更新配置示例、启动校验和相关架构文档，确保代码、配置和文档中的中文说明一致。

## 项目结构要求

1. 原则上一个公共类型只放在一个同名文件中；仅供所属类型使用的私有嵌套类型可以保留在同一文件。
2. 协议专属模型、编解码器、拆包器和服务 Worker 必须放入对应协议目录，例如 `QGdw3761` 或 `Dlt645`。
3. 只有被多个协议共同使用的类型才能放入 `Common`，禁止把单一协议实现堆放到公共目录。
4. 目录结构与命名空间必须保持一致；移动文件时同步更新命名空间、依赖注入、测试和说明文档。
5. HTTP API 统一通过 `DeviceApiEndpoints` 维护；`Program.cs` 只负责宿主配置、依赖注入、中间件和端点注册。
6. 新增存储实现时按能力分目录，例如 `Connections` 和 `Messages`，并继续依赖 Core 中的抽象接口。
7. `src` 下按部署边界使用 `Shared`、`Gateway`、`Cluster` 和 `Infrastructure` 分组；新增服务不得重新堆放到 `src` 根目录。
8. MagicOnion 接口与 MessagePack DTO 只能放在 `DeviceSocket.Rpc.Contracts`，Orleans Grain 接口与传输模型只能放在 `DeviceSocket.Cluster.Contracts`；契约项目不得引用 Host、Grain 实现或具体存储。
9. 关键跨服务调用必须传递同一个 `MsgId` 和 `TraceId`；上报与下发使用 `Direction` 区分，不因方向额外创建 `ParentMsgId`。
10. 各服务的 `Program.cs` 只负责宿主、依赖注入、中间件和端点映射；HTTP API、MagicOnion 服务和后台同步逻辑分别放入独立目录和类。
11. 设备下发统一通过 `IDeviceGrain` 暴露，普通消息方法命名为 `SendBestEffortMessageAsync`，可靠消息方法命名为 `SendReliableMessageAsync`；同类公开操作必须使用一致的 `Send` 前缀，禁止重新引入含义不明确的 `Submit`、`Push` 或 `Publish` 命名。
12. 普通消息不得创建可靠消息状态或自动重试；可靠消息必须先由 `IDeviceCommandGrain` 按 `MsgId` 持久化，再执行 Gateway 下发，并显式处理到期、重试和设备确认。
13. Gateway 协议指令 Handler 统一实现 `IProtocolInstructionHandler<TFrame, TInstructionKey>` 并放入对应协议的 `Processing/Handlers` 目录；Handler 由启动扫描自动注册，禁止重新在公共处理器中堆叠协议指令 `switch/case`。
14. 协议 Handler 运行时必须通过强类型指令键字典分发；反射扫描只允许在启动注册阶段执行，不得在单条设备消息处理路径中逐个遍历 Handler。
15. 程序运行、连接、协议异常和框架日志统一通过 `ILogger<T>` 写入；设备上报与下发原始指令统一通过 `IDeviceTrafficSink` 写入独立的上行/下行日志，禁止在程序日志中重复记录完整原始报文。
16. Gateway 的 Serilog 配置、强类型选项、Sink 注册和缓冲健康监控统一放在 `Configuration/Logging` 目录；修改日志路径、级别、滚动、保留或缓冲策略时必须同步更新 `docs/architecture/logging.md`。
17. 固定模板日志必须优先使用 `LoggerMessageAttribute` 编译时源生成，并为稳定事件分配明确的 `EventId`；高频连接、协议和通信路径禁止使用字符串插值构造日志消息。
18. 心跳通信日志默认关闭，排障时优先按设备编号临时开启；需要采样时必须使用稳定设备维度策略，日志过滤应在 `RawHex` 等大字符串生成前完成。

## 验证要求

每次代码修改完成后至少执行：

```powershell
dotnet format DeviceSocket.sln --verify-no-changes --no-restore
dotnet build DeviceSocket.sln -c Release --no-restore
dotnet test tests\DeviceSocket.Tests\DeviceSocket.Tests.csproj -c Release --no-build --no-restore
```
