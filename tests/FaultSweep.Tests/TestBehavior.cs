using FaultSweep.Effects;

namespace FaultSweep.Tests;

public sealed record TestBehavior(Func<TestState, IEffectInterceptor, CancellationToken, ValueTask> Action);
