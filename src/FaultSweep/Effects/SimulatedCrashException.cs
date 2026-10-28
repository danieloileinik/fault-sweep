namespace FaultSweep.Effects;

public sealed class SimulatedCrashException : Exception
{
    internal SimulatedCrashException(EffectPoint point, FaultKind kind)
        : base($"Simulated {kind} at effect #{point.Index}: {point.Effect}.")
    {
        Point = point;
        Kind = kind;
    }

    public EffectPoint Point { get; }

    public FaultKind Kind { get; }
}
