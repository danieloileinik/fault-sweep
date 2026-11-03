using FaultSweep.Effects;
using FaultSweep.Snapshots;

namespace FaultSweep.Execution;

internal sealed record EnvironmentRegistration(
    string Name,
    IEffectInstrumentation Instrumentation,
    SnapshotComparer Comparer,
    Func<CancellationToken, ValueTask<ISweepEnvironment>> Create
);
