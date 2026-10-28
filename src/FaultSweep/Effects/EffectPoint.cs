namespace FaultSweep.Effects;

/// <param name="Index">The one-based position within a delivery.</param>
public sealed record EffectPoint(int Index, EffectDescriptor Effect);
