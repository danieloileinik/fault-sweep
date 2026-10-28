namespace FaultSweep.Effects;

public sealed record EffectTraceEntry(int Attempt, EffectPoint Point, EffectOutcome Outcome);
