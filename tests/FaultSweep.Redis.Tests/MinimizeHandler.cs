using FaultSweep.Effects;

namespace FaultSweep.Redis.Tests;

public sealed class MinimizeHandler(MinimizeState state, IEffectInterceptor effects) : IMessageHandler<MinimizeRequest>
{
    public ValueTask HandleAsync(MinimizeRequest message, CancellationToken token)
    {
        if (message.Amount < 0)
        {
            throw new ArgumentException("Invalid candidate.");
        }

        return effects.ExecuteAsync(new EffectDescriptor("memory", "increment"), _ =>
        {
            state.Value += message.Amount;
            return ValueTask.CompletedTask;
        }, token);
    }
}
