using System.Text;

namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 将每条发送、接收和异常记录追加到按日期滚动的本地日志文件。
/// </summary>
public sealed class TrafficFileLogger(string logDirectory)
{
    private readonly object _syncRoot = new();

    /// <summary>
    /// 尽最大努力写入日志；磁盘异常不会中断 Socket 接收循环。
    /// </summary>
    /// <param name="entry">待写入的调试记录。</param>
    public void TryWrite(DebugTrafficEntry entry)
    {
        try
        {
            var line = new StringBuilder()
                .Append('[').Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append("] ")
                .Append(entry.Direction.ToString().ToUpperInvariant()).Append(' ')
                .AppendLine(entry.Title)
                .Append("HEX: ").AppendLine(string.IsNullOrWhiteSpace(entry.RawHex) ? "-" : entry.RawHex)
                .AppendLine(entry.Details)
                .AppendLine(new string('-', 80))
                .ToString();

            lock (_syncRoot)
            {
                Directory.CreateDirectory(logDirectory);
                var filePath = Path.Combine(logDirectory, $"traffic-{entry.Timestamp:yyyyMMdd}.log");
                File.AppendAllText(filePath, line, System.Text.Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志是辅助能力，磁盘只读或空间不足时必须保住调试连接。
        }
    }
}
