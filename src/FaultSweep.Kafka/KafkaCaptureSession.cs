using FaultSweep.Effects;

namespace FaultSweep.Kafka;

internal sealed class KafkaCaptureSession(IEffectInterceptor interceptor)
{
    public IEffectInterceptor Interceptor { get; } = interceptor;
    public object Gate { get; } = new();
    public Dictionary<(string Topic, int Partition), long> NextOffsets { get; } = [];
    public List<CapturedKafkaMessage> Messages { get; } = [];
}
