using FaultSweep.Effects;

namespace FaultSweep.Tests;

public sealed class TestHandler(TestState state, IEffectInterceptor effects, TestBehavior behavior)
    : IMessageHandler<TestMessage>,
        IAsyncDisposable
{
    private readonly int instance = state.RegisterHandler();

    public ValueTask HandleAsync(TestMessage message, CancellationToken cancellationToken)
    {
        state.RecordDelivery(instance);
        return behavior.Action(state, effects, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        state.DisposeHandler();
        return ValueTask.CompletedTask;
    }
}
