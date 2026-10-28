using FaultSweep.Effects;

namespace FaultSweep.Kafka;

internal sealed class KafkaCaptureSession(IEffectInterceptor interceptor)
{
    public IEffectInterceptor Interceptor { get; } = interceptor;

    public object Gate { get; } = new();

    public List<CapturedKafkaMessage> Messages { get; } = [];
}
