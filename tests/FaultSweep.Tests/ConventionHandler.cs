using FaultSweep.Effects;

namespace FaultSweep.Tests;

public sealed class ConventionHandler(TestState state, IEffectInterceptor effects)
{
    public Task HandleAsync(TestMessage message, CancellationToken token) =>
        effects
            .ExecuteAsync(
                new EffectDescriptor("memory", "Upsert"),
                _ =>
                {
                    state.Counter = message.Amount;
                    return ValueTask.CompletedTask;
                },
                token
            )
            .AsTask();
}
