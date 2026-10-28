namespace FaultSweep.Effects;

/// <summary>The operation completed, but its caller observed a timeout.</summary>
public sealed class AppliedButFailedException : TimeoutException
{
    internal AppliedButFailedException(EffectPoint point)
        : base($"Effect #{point.Index} was applied, but its acknowledgement was lost: {point.Effect}.")
    {
        Point = point;
    }

    public EffectPoint Point { get; }
}
