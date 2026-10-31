using FaultSweep.Effects;

namespace FaultSweep.Reporting;

public sealed record ReplayScenario(int EffectIndex, FaultKind Kind, int? MaxRedeliveryAttempts = null, EffectDescriptor? ExpectedEffect = null);
