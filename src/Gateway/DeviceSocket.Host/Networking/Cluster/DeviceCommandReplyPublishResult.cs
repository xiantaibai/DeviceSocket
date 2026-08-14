namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// 协议回复发布结果，隔离 Gateway Handler 对 Orleans 具体状态模型的依赖。
/// </summary>
public sealed record DeviceCommandReplyPublishResult
{
    /// <summary>Gateway 后续应采取的关联处理动作。</summary>
    public required DeviceCommandReplyPublishOutcome Outcome { get; init; }

    /// <summary>Orleans 返回的稳定结果码或本地不可用码。</summary>
    public required string Code { get; init; }
}
