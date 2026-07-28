using DeviceSocket.Core.Protocols.Common;
using DeviceSocket.Host.Networking.Processing.Common;
using Microsoft.Extensions.DependencyInjection;

namespace DeviceSocket.Tests.Gateway;

/// <summary>
/// 验证统一协议 Handler 的启动注册和运行时分发规则。
/// </summary>
public sealed class ProtocolInstructionDispatcherTests
{
    /// <summary>
    /// 精确指令键已注册时，只能执行对应 Handler。
    /// </summary>
    [Fact]
    public async Task DispatchAsync_KnownKey_UsesExactHandler()
    {
        var exact = new RecordingHandler("known");
        var fallback = new RecordingHandler("fallback");
        var dispatcher = CreateDispatcher([exact, fallback]);

        await dispatcher.DispatchAsync(CreateContext("known"));

        Assert.Equal(1, exact.CallCount);
        Assert.Equal(0, fallback.CallCount);
    }

    /// <summary>
    /// 未知指令不应抛出异常或断开连接，而是进入默认 Handler。
    /// </summary>
    [Fact]
    public async Task DispatchAsync_UnknownKey_UsesFallbackHandler()
    {
        var exact = new RecordingHandler("known");
        var fallback = new RecordingHandler("fallback");
        var dispatcher = CreateDispatcher([exact, fallback]);

        await dispatcher.DispatchAsync(CreateContext("unknown"));

        Assert.Equal(0, exact.CallCount);
        Assert.Equal(1, fallback.CallCount);
    }

    /// <summary>
    /// 两个 Handler 声明相同指令键时必须启动失败，避免运行时随机选择。
    /// </summary>
    [Fact]
    public void Constructor_DuplicateKey_Throws()
    {
        var handlers = new IProtocolInstructionHandler<TestFrame, string>[]
        {
            new RecordingHandler("known"),
            new RecordingHandler("known"),
            new RecordingHandler("fallback")
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ProtocolInstructionDispatcher<TestFrame, string>(handlers, new TestResolver()));

        Assert.Contains("存在多个 Handler", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 程序集扫描应自动找到统一泛型接口实现，不需要逐个手工注册。
    /// </summary>
    [Fact]
    public void AddProtocolInstructionHandlers_FindsImplementationsAutomatically()
    {
        var services = new ServiceCollection();

        services.AddProtocolInstructionHandlers(typeof(ProtocolInstructionDispatcherTests).Assembly);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IProtocolInstructionHandler<TestFrame, string>) &&
            descriptor.ImplementationType == typeof(ScannedTestHandler));
    }

    private static ProtocolInstructionDispatcher<TestFrame, string> CreateDispatcher(
        IEnumerable<IProtocolInstructionHandler<TestFrame, string>> handlers) =>
        new(handlers, new TestResolver());

    private static ProtocolMessageContext<TestFrame> CreateContext(string instruction) =>
        new(null!, new TestFrame(instruction), "test-msg", DateTimeOffset.UtcNow);

    private sealed class TestResolver : IProtocolInstructionResolver<TestFrame, string>
    {
        public string FallbackKey => "fallback";

        public string Resolve(TestFrame frame) => frame.Instruction;
    }

    private sealed class RecordingHandler(string key)
        : IProtocolInstructionHandler<TestFrame, string>
    {
        public string Key { get; } = key;

        public int CallCount { get; private set; }

        public ValueTask HandleAsync(
            ProtocolMessageContext<TestFrame> context,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScannedTestHandler
        : IProtocolInstructionHandler<TestFrame, string>
    {
        public string Key => "scanned";

        public ValueTask HandleAsync(
            ProtocolMessageContext<TestFrame> context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed record TestFrame(string Instruction) : IProtocolFrame
    {
        public DeviceProtocol Protocol => DeviceProtocol.QGdw3761;

        public bool IsValid => true;

        public ProtocolError? Error => null;

        public string? DeviceId => "test-device";

        public byte[] Raw => [];

        public string Summary => Instruction;
    }
}
