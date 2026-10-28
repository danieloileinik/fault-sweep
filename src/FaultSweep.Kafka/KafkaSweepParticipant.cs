using FaultSweep.Effects;

namespace FaultSweep.Kafka;

internal sealed class KafkaSweepParticipant(KafkaMessageCapture capture) : ISweepParticipant
{
    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        capture.Reset();
        return ValueTask.CompletedTask;
    }
}
