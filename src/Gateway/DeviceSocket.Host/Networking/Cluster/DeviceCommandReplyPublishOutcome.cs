namespace DeviceSocket.Host.Networking.Cluster;

/// <summary>
/// Gateway 把协议回复提交给 Orleans 后的确定性处理结果。
/// </summary>
public enum DeviceCommandReplyPublishOutcome
{
    /// <summary>Orleans 已接受回复且命令进入终态，可以删除 Gateway 关联。</summary>
    Completed = 0,

    /// <summary>设备返回失败且 Orleans 已安排正式重试，应保留 PSEQ 但关闭本次回复窗口。</summary>
    RetryScheduled = 1,

    /// <summary>Orleans 确定拒绝该回复，当前 Gateway 关联已经失效。</summary>
    Rejected = 2,

    /// <summary>Orleans 尚未应用回复但命令可能仍活动，应释放发布权等待再次确认。</summary>
    NotApplied = 3,

    /// <summary>当前 Gateway 未启用 Orleans，无法提交可靠命令回复。</summary>
    Unavailable = 4
}
