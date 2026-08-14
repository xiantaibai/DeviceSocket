namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 清理绑定到 TCP Session 的协议临时资源，避免设备断线后继续保留应答关联或分片组包状态。
/// </summary>
public interface IDeviceSessionResourceCleaner
{
    /// <summary>
    /// 删除指定 Session 的全部临时资源；实现必须幂等，允许登出和断线回调重复执行。
    /// </summary>
    /// <param name="sessionId">已经失效或即将失效的 TCP Session 编号。</param>
    void RemoveSession(string sessionId);
}
