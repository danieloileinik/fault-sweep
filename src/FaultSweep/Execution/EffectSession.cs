using FaultSweep.Effects;
using FaultSweep.Reporting;

namespace FaultSweep.Execution;

internal sealed class EffectSession(
    int attempt,
    int maxEffects,
    FaultScenario? scenario = null,
    IReadOnlyList<EffectPoint>? expected = null
)
{
    private readonly object gate = new();
    private readonly List<EffectTraceEntry> trace = [];
    private int index;
    private bool active;
    private bool closed;

    public bool FaultWasInjected { get; private set; }
    public SimulatedCrashException? Crash { get; private set; }
    public FaultSweepExecutionException? InfrastructureFailure { get; private set; }
    public IReadOnlyList<EffectTraceEntry> Trace => trace.AsReadOnly();

    public FaultSweepExecutionException Reject(string message)
    {
        lock (gate)
        {
            return Fail(message);
        }
    }

    public EffectPoint Begin(EffectDescriptor effect)
    {
        lock (gate)
        {
            if (InfrastructureFailure is not null)
            {
                throw InfrastructureFailure;
            }

            if (closed)
            {
                throw Fail(
                    "An effect ran after its delivery finished. Await all handler operations."
                );
            }

            if (Crash is not null)
            {
                throw Crash;
            }

            if (active)
            {
                throw Fail(
                    "Overlapping effects are not supported in a deterministic delivery. Await each effect."
                );
            }

            index++;
            if (index > maxEffects)
            {
                throw Fail($"The delivery exceeded the configured limit of {maxEffects} effects.");
            }

            var point = new EffectPoint(index, effect);
            if (
                !FaultWasInjected
                && expected is not null
                && (index > expected.Count || expected[index - 1].Effect != effect)
            )
            {
                throw Fail(
                    $"The effect trace diverged from the baseline at effect #{index}: {effect}."
                );
            }

            if (FaultAt(point) == FaultKind.CrashBefore)
            {
                throw Inject(point, FaultKind.CrashBefore, EffectOutcome.CrashBefore);
            }

            active = true;
            return point;
        }
    }

    private FaultKind? FaultAt(EffectPoint point)
    {
        return !FaultWasInjected && scenario?.Point.Index == point.Index ? scenario.Kind : null;
    }

    private Exception Inject(EffectPoint point, FaultKind kind, EffectOutcome outcome)
    {
        trace.Add(new EffectTraceEntry(attempt, point, outcome));
        FaultWasInjected = true;
        if (kind == FaultKind.AppliedButFailed)
        {
            return new AppliedButFailedException(point);
        }

        Crash = new SimulatedCrashException(point, kind);
        return Crash;
    }

    public void Complete(EffectPoint point, bool succeeded)
    {
        lock (gate)
        {
            active = false;
            if (succeeded)
            {
                switch (FaultAt(point))
                {
                    case FaultKind.CrashAfter:
                        throw Inject(point, FaultKind.CrashAfter, EffectOutcome.CrashAfter);
                    case FaultKind.AppliedButFailed:
                        throw Inject(
                            point,
                            FaultKind.AppliedButFailed,
                            EffectOutcome.AppliedButFailed
                        );
                }
            }

            trace.Add(
                new EffectTraceEntry(
                    attempt,
                    point,
                    succeeded ? EffectOutcome.Succeeded : EffectOutcome.Failed
                )
            );
        }
    }

    public void Close()
    {
        lock (gate)
        {
            closed = true;
            if (active)
            {
                Fail(
                    "The handler returned while an effect was still running. Await all handler operations."
                );
            }
        }
    }

    private FaultSweepExecutionException Fail(string message)
    {
        InfrastructureFailure ??= new FaultSweepExecutionException(message);
        return InfrastructureFailure;
    }
}
