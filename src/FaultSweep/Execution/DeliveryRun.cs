using FaultSweep.Effects;
using FaultSweep.Reporting;

namespace FaultSweep.Execution;

internal sealed record DeliveryRun(
    IReadOnlyList<DeliveryAttempt> Attempts,
    IReadOnlyList<EffectTraceEntry> Trace,
    bool FaultWasInjected
);
