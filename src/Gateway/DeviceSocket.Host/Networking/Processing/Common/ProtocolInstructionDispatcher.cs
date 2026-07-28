using DeviceSocket.Core.Protocols.Common;

namespace DeviceSocket.Host.Networking.Processing.Common;

/// <summary>
/// 在启动时构建指令键索引，运行时以 O(1) 查找分发到唯一 Handler。
/// </summary>
public sealed class ProtocolInstructionDispatcher<TFrame, TInstructionKey>
    where TFrame : IProtocolFrame
    where TInstructionKey : notnull
{
    private readonly IReadOnlyDictionary<TInstructionKey, IProtocolInstructionHandler<TFrame, TInstructionKey>> _handlers;
    private readonly IProtocolInstructionResolver<TFrame, TInstructionKey> _resolver;

    /// <summary>
    /// 创建指令分发器；重复键或缺少默认 Handler 时在启动阶段立即报错。
    /// </summary>
    public ProtocolInstructionDispatcher(
        IEnumerable<IProtocolInstructionHandler<TFrame, TInstructionKey>> handlers,
        IProtocolInstructionResolver<TFrame, TInstructionKey> resolver)
    {
        _resolver = resolver;
        var groups = handlers.GroupBy(handler => handler.Key).ToArray();
        var duplicate = groups.FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
            throw new InvalidOperationException($"协议指令键 {duplicate.Key} 存在多个 Handler。");

        _handlers = groups.ToDictionary(group => group.Key, group => group.Single());
        if (!_handlers.ContainsKey(_resolver.FallbackKey))
            throw new InvalidOperationException($"协议指令分发缺少默认 Handler: {_resolver.FallbackKey}。");
    }

    /// <summary>
    /// 按指令键查找 Handler；未注册的指令交给默认 Handler，不因未知业务指令断开连接。
    /// </summary>
    public ValueTask DispatchAsync(
        ProtocolMessageContext<TFrame> context,
        CancellationToken cancellationToken = default)
    {
        var key = _resolver.Resolve(context.Frame);
        var handler = _handlers.TryGetValue(key, out var matched)
            ? matched
            : _handlers[_resolver.FallbackKey];
        return handler.HandleAsync(context, cancellationToken);
    }
}
