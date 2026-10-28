using FaultSweep.Effects;

namespace FaultSweep.Reporting;

public sealed record FaultScenario(EffectPoint Point, FaultKind Kind);
