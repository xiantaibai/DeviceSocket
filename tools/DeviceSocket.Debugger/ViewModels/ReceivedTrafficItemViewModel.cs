using System.Text;
using DeviceSocket.Debugger.Core.Networking;

namespace DeviceSocket.Debugger.ViewModels;

/// <summary>
/// 表示接收中心中的一条紧凑通信记录，并提供分类、筛选摘要和完整详情。
/// </summary>
public sealed class ReceivedTrafficItemViewModel
{
    private readonly string _searchableText;

    /// <summary>
    /// 根据 TCP 通信记录和可选关联结果创建接收中心展示项。
    /// </summary>
    /// <param name="entry">包含协议元数据、原始报文和解析详情的通信记录。</param>
    /// <param name="correlation">请求回复关联结果；不提供时沿用记录原始分类。</param>
    /// <exception cref="ArgumentNullException">通信记录为 null 时抛出。</exception>
    public ReceivedTrafficItemViewModel(
        DebugTrafficEntry entry,
        DebugTrafficCorrelationResult? correlation = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Category = correlation?.Category ?? entry.Category;
        Timestamp = entry.Timestamp;
        TimeText = entry.Timestamp.ToString("HH:mm:ss.fff");
        CategoryText = GetCategoryText(Category);
        Title = entry.Title;
        ProtocolText = entry.Metadata?.Protocol.ToString() ?? "-";
        DeviceText = string.IsNullOrWhiteSpace(entry.Metadata?.DeviceId)
            ? "未知设备"
            : entry.Metadata.DeviceId;
        InstructionText = string.IsNullOrWhiteSpace(entry.Metadata?.InstructionKey)
            ? "无指令标识"
            : entry.Metadata.InstructionKey;
        ResponseStatus = correlation?.StatusText ?? GetDefaultStatus(Category);
        RawHex = string.IsNullOrWhiteSpace(entry.RawHex) ? "-" : entry.RawHex;
        Details = string.IsNullOrWhiteSpace(entry.Details) ? "-" : entry.Details;
        SecondaryText = $"{DeviceText} · {InstructionText} · {ResponseStatus}";
        FullText = BuildFullText(entry, correlation);
        CharacterCount = FullText.Length;
        _searchableText = string.Join(
            '\n',
            Title,
            DeviceText,
            InstructionText,
            RawHex,
            Details,
            ResponseStatus);
    }

    /// <summary>获取记录产生时间。</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>获取列表使用的毫秒级时间文本。</summary>
    public string TimeText { get; }

    /// <summary>获取请求回复关联后的最终分类。</summary>
    public DebugTrafficCategory Category { get; }

    /// <summary>获取分类的中文显示名称。</summary>
    public string CategoryText { get; }

    /// <summary>获取通信记录标题。</summary>
    public string Title { get; }

    /// <summary>获取协议显示名称；状态记录无法识别协议时为“-”。</summary>
    public string ProtocolText { get; }

    /// <summary>获取标准化设备编号；无法解析时显示“未知设备”。</summary>
    public string DeviceText { get; }

    /// <summary>获取 AFN/FN、功能码或数据标识组成的指令筛选键。</summary>
    public string InstructionText { get; }

    /// <summary>获取匹配状态和响应耗时。</summary>
    public string ResponseStatus { get; }

    /// <summary>获取列表第二行的设备、指令和关联状态摘要。</summary>
    public string SecondaryText { get; }

    /// <summary>获取完整原始报文 HEX；无报文时为“-”。</summary>
    public string RawHex { get; }

    /// <summary>获取协议解析详情或状态说明。</summary>
    public string Details { get; }

    /// <summary>获取详情面板展示的完整通信记录。</summary>
    public string FullText { get; }

    /// <summary>获取完整展示文本的字符数，用于限制接收中心内存占用。</summary>
    public int CharacterCount { get; }

    /// <summary>获取当前记录是否为普通业务消息。</summary>
    public bool IsBusiness => Category == DebugTrafficCategory.Business;

    /// <summary>获取当前记录是否为心跳消息。</summary>
    public bool IsHeartbeat => Category == DebugTrafficCategory.Heartbeat;

    /// <summary>获取当前记录是否为异常消息。</summary>
    public bool IsError => Category == DebugTrafficCategory.Error;

    /// <summary>获取当前记录是否为系统或链路状态消息。</summary>
    public bool IsSystem => Category == DebugTrafficCategory.System;

    /// <summary>
    /// 判断记录是否包含用户输入的设备号、标题、指令、HEX、详情或关联状态关键字。
    /// </summary>
    /// <param name="searchText">搜索文本；空白表示不过滤。</param>
    /// <returns>匹配或未设置搜索条件时返回 true。</returns>
    public bool MatchesSearch(string? searchText) =>
        string.IsNullOrWhiteSpace(searchText) ||
        _searchableText.Contains(searchText.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把分类枚举转换为列表标签文字。
    /// </summary>
    /// <param name="category">通信记录分类。</param>
    /// <returns>分类中文名称。</returns>
    private static string GetCategoryText(DebugTrafficCategory category) => category switch
    {
        DebugTrafficCategory.Business => "业务",
        DebugTrafficCategory.Heartbeat => "心跳",
        DebugTrafficCategory.Error => "异常",
        _ => "状态"
    };

    /// <summary>
    /// 为没有经过关联器的记录生成默认状态说明。
    /// </summary>
    /// <param name="category">通信记录分类。</param>
    /// <returns>适合详情区展示的状态文本。</returns>
    private static string GetDefaultStatus(DebugTrafficCategory category) => category switch
    {
        DebugTrafficCategory.Business => "主动上报 / 未匹配",
        DebugTrafficCategory.Heartbeat => "心跳消息",
        DebugTrafficCategory.Error => "异常消息",
        _ => "状态消息"
    };

    /// <summary>
    /// 构造可复制的完整详情，集中呈现分类、设备、指令、匹配状态、HEX 和解析结果。
    /// </summary>
    /// <param name="entry">原始通信记录。</param>
    /// <param name="correlation">可选的请求回复关联结果。</param>
    /// <returns>多行完整详情文本。</returns>
    private static string BuildFullText(
        DebugTrafficEntry entry,
        DebugTrafficCorrelationResult? correlation)
    {
        var category = correlation?.Category ?? entry.Category;
        var metadata = entry.Metadata;
        var builder = new StringBuilder()
            .Append("时间：").AppendLine(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append("分类：").AppendLine(GetCategoryText(category))
            .Append("标题：").AppendLine(entry.Title)
            .Append("协议：").AppendLine(metadata?.Protocol.ToString() ?? "-")
            .Append("设备：").AppendLine(string.IsNullOrWhiteSpace(metadata?.DeviceId) ? "未知设备" : metadata.DeviceId)
            .Append("指令：").AppendLine(string.IsNullOrWhiteSpace(metadata?.InstructionKey) ? "无指令标识" : metadata.InstructionKey)
            .Append("关联：").AppendLine(correlation?.StatusText ?? GetDefaultStatus(category))
            .AppendLine("HEX：")
            .AppendLine(string.IsNullOrWhiteSpace(entry.RawHex) ? "-" : entry.RawHex)
            .AppendLine("解析结果：")
            .Append(string.IsNullOrWhiteSpace(entry.Details) ? "-" : entry.Details);
        return builder.ToString();
    }
}
