# DeviceSocket 协议调试工具

这是一个基于 .NET 8 和 Avalonia 的 Windows 桌面调试工具。它与压测工具分离，当前用于单连接验证 Q/GDW 376.1-2012 和 DL/T 645-2007 指令生成、发送和响应解析。

## 启动

在仓库根目录执行：

```powershell
dotnet run --project tools\DeviceSocket.Debugger\DeviceSocket.Debugger.csproj
```

或发布 Windows x64 自包含版本：

```powershell
dotnet publish tools\DeviceSocket.Debugger\DeviceSocket.Debugger.csproj `
  -c Release -r win-x64 --self-contained true
```

启动后先在右侧选择指令，按需修改参数；协议和默认端口会随指令切换。建立 TCP 连接后点击“发送指令”，界面会自动切换到接收页。

发送、接收、连接状态和解析错误会写入：

```text
%LOCALAPPDATA%\DeviceSocket\Debugger\Logs
```

## 指令集

内置示例位于 `CommandSets`：

- `QGdw3761/basic-commands.json`：登录、心跳、退出登录和自定义 HEX。
- `Dlt645/basic-commands.json`：读取通信地址、读取数据、自定义 645 帧和自定义 HEX。

工具启动时自动加载随程序发布的指令集，也可以通过“打开 JSON”临时加载其他文件。修改文件后点击“重新加载”即可生效。

一条指令支持三种模式：

- `layout`：按字段顺序声明固定值、输入值、复制、长度和校验和，不需要编写 C#。
- `codec`：调用安全白名单中的协议编码器，自动计算地址字节序、长度、数据域变换和校验和。
- `raw`：替换 `{{参数名}}` 后直接发送 HEX，适合复现已有现场报文。

`fixedValues` 用于指令固定参数，不在界面显示；`inputs` 会自动生成输入框或下拉框。当前白名单编码器为：

- `qgdw3761.link`
- `dlt645.frame`

完整字段约束见 [debugger-command-set.schema.json](../../docs/debugger-command-set.schema.json)。

## 错误处理

- JSON 语法或语义错误只会阻止对应文件加载。
- 参数或 HEX 错误会显示在接收页，不会发送数据。
- 半包会继续缓存，粘包会逐帧解析。
- 非法帧保留原始 HEX 和协议错误原因，后续帧仍继续处理。
- 接收缓冲超过 1 MiB 时会记录尾部数据并重置，避免异常输入持续占用内存。
