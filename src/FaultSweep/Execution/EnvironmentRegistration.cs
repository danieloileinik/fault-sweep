using FaultSweep.Effects;

namespace FaultSweep.Execution;

internal sealed record EnvironmentRegistration(
    string Name,
    IEffectInstrumentation Instrumentation,
    Func<CancellationToken, ValueTask<ISweepEnvironment>> Create
);
