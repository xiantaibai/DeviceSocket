using DeviceSocket.Cluster.Contracts.Commands;
using DeviceSocket.Cluster.Contracts.Devices;
using DeviceSocket.Cluster.Contracts.Gateways;
using DeviceSocket.Cluster.Contracts.Scheduling;
using DeviceSocket.Cluster.Grains.Scheduling;
using DeviceSocket.Rpc.Contracts.Gateway;
using System.Security.Cryptography;

namespace DeviceSocket.Tests.Cluster;

/// <summary>
/// 在真实 Orleans 测试 Silo 中验证设备一问一答、延迟、超时重试、优先级和旧 Session 防护。
/// </summary>
[Collection(OrleansCommandClusterCollection.Name)]
public sealed class ReliableCommandOrleansIntegrationTests(OrleansCommandClusterFixture fixture)
{
    /// <summary>
    /// 验证第一条命令等待回复期间第二条不会下发，成功回复后设备通道才发送下一条。
    /// </summary>
    [Fact]
    public async Task DeviceGrain_SendsNextCommandOnlyAfterCurrentReply()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var first = NewCommand("serial-first", responseTimeout: TimeSpan.FromSeconds(5));
        var second = NewCommand("serial-second", responseTimeout: TimeSpan.FromSeconds(5));

        var firstReceipt = await device.SendReliableMessageAsync(first);
        var secondReceipt = await device.SendReliableMessageAsync(second);

        Assert.True(firstReceipt.Accepted);
        Assert.True(secondReceipt.Accepted);
        Assert.Single(gateway.FindByDevice(deviceId));

        var route = await device.GetRouteAsync();
        var firstRequest = Assert.Single(gateway.Find(deviceId, first.MsgId));
        var replyReceipt = await device.ReceiveCommandReplyAsync(new DeviceCommandReply
        {
            DeviceId = deviceId,
            MsgId = first.MsgId,
            AttemptNo = firstRequest.AttemptNo,
            SessionEpoch = route.SessionEpoch,
            Success = true,
            ReceivedAt = DateTimeOffset.UtcNow,
            Message = "第一条命令执行成功。"
        });

        Assert.Equal(ReliableMessageStatus.Confirmed, replyReceipt.Status);
        await WaitUntilAsync(() => gateway.Find(deviceId, second.MsgId).Length == 1, TimeSpan.FromSeconds(3));
        var requests = gateway.FindByDevice(deviceId);
        Assert.Equal([first.MsgId, second.MsgId], requests.Select(request => request.MsgId).ToArray());
    }

    /// <summary>
    /// 验证 NotBefore 到达前不会下发，到期后由固定分片 SchedulerGrain 唤醒设备。
    /// </summary>
    [Fact]
    public async Task Scheduler_DelaysCommandUntilNotBefore()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var command = NewCommand("delayed", responseTimeout: TimeSpan.FromSeconds(5));
        command.NotBefore = DateTimeOffset.UtcNow.AddMilliseconds(250);

        var receipt = await device.SendReliableMessageAsync(command);

        Assert.True(receipt.Accepted);
        Assert.Empty(gateway.Find(deviceId, command.MsgId));
        await WaitUntilAsync(() => gateway.Find(deviceId, command.MsgId).Length == 1, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 验证设备未回复时按同一设备通道自动重试，并在新尝试回复后进入 Confirmed 终态。
    /// </summary>
    [Fact]
    public async Task Scheduler_RetriesAfterReplyTimeout_AndAcceptsCurrentAttemptReply()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var command = NewCommand("retry", responseTimeout: TimeSpan.FromMilliseconds(80));
        command.MaxAttempts = 3;

        await device.SendReliableMessageAsync(command);
        await WaitUntilAsync(() => gateway.Find(deviceId, command.MsgId).Length >= 2, TimeSpan.FromSeconds(4));

        var attempts = gateway.Find(deviceId, command.MsgId);
        Assert.Equal(1, attempts[0].AttemptNo);
        Assert.Equal(2, attempts[1].AttemptNo);
        var route = await device.GetRouteAsync();
        var receipt = await device.ReceiveCommandReplyAsync(new DeviceCommandReply
        {
            DeviceId = deviceId,
            MsgId = command.MsgId,
            AttemptNo = attempts[1].AttemptNo,
            SessionEpoch = route.SessionEpoch,
            Success = true,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(ReliableMessageStatus.Confirmed, receipt.Status);
    }

    /// <summary>
    /// 验证达到 MaxAttempts 后不再无限重试，命令进入 Failed 并释放设备通道。
    /// </summary>
    [Fact]
    public async Task Scheduler_StopsRetryingAfterMaxAttempts()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var command = NewCommand("max-attempts", responseTimeout: TimeSpan.FromMilliseconds(60));
        command.MaxAttempts = 2;

        await device.SendReliableMessageAsync(command);
        var commandGrain = fixture.Cluster.GrainFactory.GetGrain<IDeviceCommandGrain>(command.MsgId);
        await WaitUntilAsync(
            async () => (await commandGrain.GetSnapshotAsync()).Status == ReliableMessageStatus.Failed,
            TimeSpan.FromSeconds(4));

        Assert.Equal(2, gateway.Find(deviceId, command.MsgId).Length);
    }

    /// <summary>
    /// 验证同设备批量命令按优先级选择队首，相同优先级再按设备顺序号执行。
    /// </summary>
    [Fact]
    public async Task BatchSend_UsesPriorityWithoutPreemptingActiveCommand()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var low = NewCommand("priority-low", priority: 1, responseTimeout: TimeSpan.FromSeconds(5));
        var high = NewCommand("priority-high", priority: 100, responseTimeout: TimeSpan.FromSeconds(5));
        var medium = NewCommand("priority-medium", priority: 10, responseTimeout: TimeSpan.FromSeconds(5));

        var batch = await device.SendReliableMessagesAsync([low, high, medium]);

        Assert.Equal(3, batch.AcceptedCount);
        var first = Assert.Single(gateway.FindByDevice(deviceId));
        Assert.Equal(high.MsgId, first.MsgId);
        var route = await device.GetRouteAsync();
        await ReplySuccessAsync(device, deviceId, route.SessionEpoch, first);
        await WaitUntilAsync(() => gateway.FindByDevice(deviceId).Length == 2, TimeSpan.FromSeconds(3));
        var second = gateway.FindByDevice(deviceId)[1];
        Assert.Equal(medium.MsgId, second.MsgId);
        await ReplySuccessAsync(device, deviceId, route.SessionEpoch, second);
        await WaitUntilAsync(() => gateway.FindByDevice(deviceId).Length == 3, TimeSpan.FromSeconds(3));
        Assert.Equal(low.MsgId, gateway.FindByDevice(deviceId)[2].MsgId);
    }

    /// <summary>
    /// 验证设备重连后旧 Session 的迟到回复不会确认当前命令。
    /// </summary>
    [Fact]
    public async Task DeviceGrain_RejectsReplyFromPreviousSessionEpoch()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var command = NewCommand("stale-session", responseTimeout: TimeSpan.FromSeconds(5));
        await device.SendReliableMessageAsync(command);
        var request = Assert.Single(gateway.Find(deviceId, command.MsgId));
        var oldRoute = await device.GetRouteAsync();
        await RegisterDeviceRouteAsync(device, "session-reconnected");

        var receipt = await device.ReceiveCommandReplyAsync(new DeviceCommandReply
        {
            DeviceId = deviceId,
            MsgId = command.MsgId,
            AttemptNo = request.AttemptNo,
            SessionEpoch = oldRoute.SessionEpoch,
            Success = true,
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.False(receipt.Accepted);
        Assert.Equal("STALE_SESSION_REPLY", receipt.Code);
        var snapshot = await fixture.Cluster.GrainFactory
            .GetGrain<IDeviceCommandGrain>(command.MsgId)
            .GetSnapshotAsync();
        Assert.Equal(ReliableMessageStatus.AwaitingConfirmation, snapshot.Status);
    }

    /// <summary>
    /// 验证相同 MsgId 的相同内容幂等返回，而报文内容变化会稳定拒绝。
    /// </summary>
    [Fact]
    public async Task ReliableSend_IsIdempotentAndRejectsMsgIdContentConflict()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var original = NewCommand("idempotent", responseTimeout: TimeSpan.FromSeconds(5));

        var first = await device.SendReliableMessageAsync(original);
        var duplicate = await device.SendReliableMessageAsync(original);
        var conflictCommand = NewCommand("conflict", responseTimeout: TimeSpan.FromSeconds(5));
        conflictCommand.MsgId = original.MsgId;
        conflictCommand.Payload = [0x68, 0x02, 0x16];
        var conflict = await device.SendReliableMessageAsync(conflictCommand);

        Assert.True(first.Accepted);
        Assert.True(duplicate.Accepted);
        Assert.False(conflict.Accepted);
        Assert.Equal("MSG_ID_CONTENT_CONFLICT", conflict.Code);
    }

    /// <summary>
    /// 验证未来高优先级命令不会阻塞已经到期的低优先级命令。
    /// </summary>
    [Fact]
    public async Task DelayedHighPriorityCommand_DoesNotBlockReadyCommand()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var delayed = NewCommand("future-high", priority: 100, responseTimeout: TimeSpan.FromSeconds(5));
        delayed.NotBefore = DateTimeOffset.UtcNow.AddSeconds(2);
        var ready = NewCommand("ready-low", priority: 1, responseTimeout: TimeSpan.FromSeconds(5));

        var batch = await device.SendReliableMessagesAsync([delayed, ready]);

        Assert.Equal(2, batch.AcceptedCount);
        var first = Assert.Single(gateway.FindByDevice(deviceId));
        Assert.Equal(ready.MsgId, first.MsgId);
    }

    /// <summary>
    /// 验证命令整体 TTL 先于下一次重试到达时进入 Expired 并停止继续下发。
    /// </summary>
    [Fact]
    public async Task Scheduler_ExpiresCommandAtOverallDeadline()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var command = NewCommand("expires", responseTimeout: TimeSpan.FromSeconds(2));
        command.ExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(180);
        command.MaxAttempts = 10;

        await device.SendReliableMessageAsync(command);
        var commandGrain = fixture.Cluster.GrainFactory.GetGrain<IDeviceCommandGrain>(command.MsgId);
        await WaitUntilAsync(
            async () => (await commandGrain.GetSnapshotAsync()).Status == ReliableMessageStatus.Expired,
            TimeSpan.FromSeconds(3));

        Assert.Single(gateway.Find(deviceId, command.MsgId));
    }

    /// <summary>
    /// 验证通过 DeviceGrain 取消 Active 命令会立即释放通道并发送下一条等待命令。
    /// </summary>
    [Fact]
    public async Task CancelActiveCommand_ReleasesDeviceLaneImmediately()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var active = NewCommand("cancel-active", responseTimeout: TimeSpan.FromSeconds(5));
        var waiting = NewCommand("after-cancel", responseTimeout: TimeSpan.FromSeconds(5));
        await device.SendReliableMessageAsync(active);
        await device.SendReliableMessageAsync(waiting);

        var receipt = await device.CancelReliableMessageAsync(active.MsgId);

        Assert.Equal(ReliableMessageStatus.Cancelled, receipt.Status);
        await WaitUntilAsync(() => gateway.Find(deviceId, waiting.MsgId).Length == 1, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// 验证 CommandGrain 持久化后尚未被设备接管的命令，可由分片恢复扫描幂等补入设备通道。
    /// </summary>
    [Fact]
    public async Task Scheduler_RepairsCommandInterruptedBeforeDeviceEnqueue()
    {
        var deviceId = NewDeviceId();
        await RegisterOnlineDeviceAsync(deviceId);
        var gateway = GetGatewayInvoker();
        var msgId = $"orphan-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow.AddMilliseconds(-100);
        var shardId = DeviceCommandShardResolver.Resolve(deviceId, 16);
        var command = fixture.Cluster.GrainFactory.GetGrain<IDeviceCommandGrain>(msgId);
        var payload = new byte[] { 0x68, 0x03, 0x16 };
        var created = await command.CreateAsync(new ReliableCommandRegistration
        {
            DeviceId = deviceId,
            Protocol = "QGdw3761",
            Payload = payload,
            PayloadHash = Convert.ToHexString(SHA256.HashData(payload)),
            TraceId = msgId,
            CreatedAt = now,
            NotBefore = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ResponseTimeout = TimeSpan.FromSeconds(5),
            Priority = 0,
            DeviceSequence = 1,
            MaxAttempts = 3,
            SchedulerShard = shardId,
            RequireDeviceConfirmation = true
        });
        Assert.Equal(ReliableMessageStatus.EnqueuePending, created.Snapshot.Status);

        await fixture.Cluster.GrainFactory
            .GetGrain<IDeviceCommandSchedulerGrain>(shardId)
            .RecoverAsync();

        await WaitUntilAsync(() => gateway.Find(deviceId, msgId).Length == 1, TimeSpan.FromSeconds(3));
        var snapshot = await command.GetSnapshotAsync();
        Assert.Equal(ReliableMessageStatus.AwaitingConfirmation, snapshot.Status);
    }

    /// <summary>
    /// 验证 SchedulerGrain 丢弃同设备旧版本通知，避免重复 Timer 唤醒覆盖最新时间。
    /// </summary>
    [Fact]
    public async Task Scheduler_DiscardsStaleScheduleVersion()
    {
        var shardId = Random.Shared.Next(0, 16);
        var scheduler = fixture.Cluster.GrainFactory.GetGrain<IDeviceCommandSchedulerGrain>(shardId);
        var deviceId = NewDeviceId();
        var dueAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await scheduler.ScheduleAsync(new DeviceCommandSchedule
        {
            DeviceId = deviceId,
            ScheduleVersion = 2,
            DueAt = dueAt
        });
        await WaitUntilAsync(
            async () => (await scheduler.GetSnapshotAsync()).ScheduledDeviceCount >= 1,
            TimeSpan.FromSeconds(3));
        var before = await scheduler.GetSnapshotAsync();
        await scheduler.ScheduleAsync(new DeviceCommandSchedule
        {
            DeviceId = deviceId,
            ScheduleVersion = 1,
            DueAt = dueAt.AddMinutes(-1)
        });

        await WaitUntilAsync(
            async () => (await scheduler.GetSnapshotAsync()).StaleScheduleCount > before.StaleScheduleCount,
            TimeSpan.FromSeconds(3));
        var snapshot = await scheduler.GetSnapshotAsync();
        Assert.True(snapshot.StaleScheduleCount > before.StaleScheduleCount);
    }

    /// <summary>
    /// 验证终态时间到达前拒绝维护，到达后先压缩 Payload，再物理删除完整 CommandGrain 状态。
    /// </summary>
    [Fact]
    public async Task CommandCleanup_RechecksTerminalTimeBeforeCompactionAndPurge()
    {
        var deviceId = NewDeviceId();
        var device = await RegisterOnlineDeviceAsync(deviceId);
        var command = NewCommand("cleanup", responseTimeout: TimeSpan.FromSeconds(5));

        await device.SendReliableMessageAsync(command);
        var request = Assert.Single(GetGatewayInvoker().Find(deviceId, command.MsgId));
        var route = await device.GetRouteAsync();
        var commandGrain = fixture.Cluster.GrainFactory.GetGrain<IDeviceCommandGrain>(command.MsgId);
        Assert.False(await commandGrain.CompactPayloadAsync(DateTimeOffset.MaxValue));
        Assert.False(await commandGrain.PurgeAsync(DateTimeOffset.MaxValue));
        await ReplySuccessAsync(device, deviceId, route.SessionEpoch, request);

        var terminal = await commandGrain.GetSnapshotAsync();
        var terminalAt = Assert.IsType<DateTimeOffset>(terminal.TerminalAt);
        Assert.NotEmpty(terminal.Payload);
        Assert.False(await commandGrain.CompactPayloadAsync(terminalAt.AddTicks(-1)));

        Assert.True(await commandGrain.CompactPayloadAsync(terminalAt));
        var compacted = await commandGrain.GetSnapshotAsync();
        Assert.Empty(compacted.Payload);
        Assert.NotNull(compacted.PayloadCompactedAt);
        Assert.Equal(terminal.PayloadHash, compacted.PayloadHash);
        Assert.False(await commandGrain.PurgeAsync(terminalAt.AddTicks(-1)));

        Assert.True(await commandGrain.PurgeAsync(terminalAt));
        var deleted = await commandGrain.GetSnapshotAsync();
        Assert.Equal(command.MsgId, deleted.MsgId);
        Assert.Empty(deleted.DeviceId);
    }

    /// <summary>
    /// 注册测试 Gateway 节点和设备路由，返回已在线的 DeviceGrain。
    /// </summary>
    /// <param name="deviceId">唯一测试设备编号。</param>
    /// <returns>已注册有效路由的 DeviceGrain。</returns>
    private async Task<IDeviceGrain> RegisterOnlineDeviceAsync(string deviceId)
    {
        var now = DateTimeOffset.UtcNow;
        await fixture.Cluster.GrainFactory.GetGrain<IGatewayNodeGrain>("gateway-test").RegisterOrRenewAsync(
            new GatewayNodeRegistration
            {
                InstanceId = "instance-test",
                RpcEndpoint = "https://gateway.test",
                SupportedProtocols = ["QGdw3761"],
                StartedAt = now,
                LeaseExpiresAt = now.AddMinutes(10)
            });
        var device = fixture.Cluster.GrainFactory.GetGrain<IDeviceGrain>(deviceId);
        await RegisterDeviceRouteAsync(device, $"session-{Guid.NewGuid():N}");
        return device;
    }

    /// <summary>
    /// 为同一个 DeviceGrain 注册一个新的在线 Session。
    /// </summary>
    /// <param name="device">目标 DeviceGrain。</param>
    /// <param name="sessionId">新的 Session 编号。</param>
    private static async Task RegisterDeviceRouteAsync(IDeviceGrain device, string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        await device.RegisterConnectionAsync(new DeviceRouteRegistration
        {
            GatewayNodeId = "gateway-test",
            GatewayRpcEndpoint = "https://gateway.test",
            SessionId = sessionId,
            Protocol = "QGdw3761",
            ConnectedAt = now,
            LeaseExpiresAt = now.AddMinutes(10)
        });
    }

    /// <summary>
    /// 构造具有唯一 MsgId 和稳定 Payload 的可靠测试命令。
    /// </summary>
    /// <param name="name">便于失败诊断的命令名称。</param>
    /// <param name="priority">设备队列优先级。</param>
    /// <param name="responseTimeout">设备回复超时。</param>
    /// <returns>可靠命令。</returns>
    private static ReliableDeviceMessage NewCommand(
        string name,
        int priority = 0,
        TimeSpan? responseTimeout = null) => new()
        {
            MsgId = $"{name}-{Guid.NewGuid():N}",
            Protocol = "QGdw3761",
            Payload = [0x68, 0x01, 0x16],
            TraceId = $"trace-{Guid.NewGuid():N}",
            Priority = priority,
            ResponseTimeout = responseTimeout,
            MaxAttempts = 3,
            RequireDeviceConfirmation = true
        };

    /// <summary>
    /// 调用设备回复入口确认指定 Gateway 请求。
    /// </summary>
    /// <param name="device">目标 DeviceGrain。</param>
    /// <param name="deviceId">设备编号。</param>
    /// <param name="sessionEpoch">当前连接代次。</param>
    /// <param name="request">待确认的 Gateway 请求。</param>
    private static async Task ReplySuccessAsync(
        IDeviceGrain device,
        string deviceId,
        long sessionEpoch,
        SendCommandRequest request)
    {
        await device.ReceiveCommandReplyAsync(new DeviceCommandReply
        {
            DeviceId = deviceId,
            MsgId = request.MsgId,
            AttemptNo = request.AttemptNo,
            SessionEpoch = sessionEpoch,
            Success = true,
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// 从主测试 Silo 取得共享 Gateway 替身。
    /// </summary>
    /// <returns>记录全部发送请求的替身实例。</returns>
    private static TestGatewayCommandInvoker GetGatewayInvoker() => new();

    /// <summary>
    /// 轮询同步条件直到成功或超时。
    /// </summary>
    /// <param name="condition">无副作用的成功条件。</param>
    /// <param name="timeout">最大等待时间。</param>
    /// <exception cref="TimeoutException">截止时间前条件未满足。</exception>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"等待条件超过 {timeout}。");
    }

    /// <summary>
    /// 轮询异步条件直到成功或超时。
    /// </summary>
    /// <param name="condition">异步成功条件。</param>
    /// <param name="timeout">最大等待时间。</param>
    /// <exception cref="TimeoutException">截止时间前条件未满足。</exception>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"等待条件超过 {timeout}。");
    }

    /// <summary>
    /// 创建不会与其他共享 Silo 用例冲突的设备编号。
    /// </summary>
    /// <returns>唯一设备编号。</returns>
    private static string NewDeviceId() => $"device-{Guid.NewGuid():N}";
}
