using FaultSweep.Effects;

namespace FaultSweep.Demo;

public sealed class PaymentHandler(PaymentState state, IEffectInterceptor effects)
    : IMessageHandler<Payment>
{
    public ValueTask HandleAsync(Payment message, CancellationToken cancellationToken)
    {
        return effects.ExecuteAsync(
            new EffectDescriptor("payments", "Commit", message.PaymentId.ToString()),
            _ =>
            {
                state.Apply(message);
                return ValueTask.CompletedTask;
            },
            cancellationToken
        );
    }
}
