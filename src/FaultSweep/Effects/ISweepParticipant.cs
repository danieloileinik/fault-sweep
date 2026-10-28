namespace FaultSweep.Effects;

/// <summary>Resets adapter-owned state before the baseline and each independent fault scenario.</summary>
public interface ISweepParticipant
{
    ValueTask ResetAsync(CancellationToken cancellationToken);
}
