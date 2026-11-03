using FaultSweep.Effects;
using FaultSweep.Reporting;

namespace FaultSweep.Execution;

internal sealed record DeliveryPlan(
    int MaxEffects,
    int MaxRedeliveryAttempts,
    int Delivery = 1,
    int DeliveryCount = 1,
    FaultScenario? Fault = null,
    IReadOnlyList<EffectPoint>? Expected = null
);
