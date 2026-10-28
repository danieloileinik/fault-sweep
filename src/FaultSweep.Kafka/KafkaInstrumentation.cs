using FaultSweep.Effects;

namespace FaultSweep.Kafka;

internal sealed class KafkaInstrumentation(KafkaMessageCapture capture) : IEffectInstrumentation
{
    public IDisposable Activate(IEffectInterceptor interceptor) => capture.Activate(interceptor);
}
