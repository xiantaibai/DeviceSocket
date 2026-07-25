namespace DeviceSocket.Debugger.Core.Networking;

/// <summary>
/// 提供只用于错误日志截断的集合辅助方法。
/// </summary>
internal static class CollectionsMarshalHelper
{
    /// <summary>
    /// 复制集合尾部的有限字节，避免异常日志包含无限增长的数据。
    /// </summary>
    internal static byte[] TakeTail(List<byte> source, int maximumLength)
    {
        var count = Math.Min(source.Count, maximumLength);
        return source.GetRange(source.Count - count, count).ToArray();
    }
}
