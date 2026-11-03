using FaultSweep.Reporting;

namespace FaultSweep.Effects;

public sealed record EffectTraceEntry(int Attempt, EffectPoint Point, EffectOutcome Outcome)
{
    public DeliveryIdentity Identity { get; init; } = new(1, Attempt);
}
