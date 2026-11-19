using System.Collections.ObjectModel;
using FaultSweep.Effects;
using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal sealed class SweepState(
    IServiceProvider services,
    Func<IServiceProvider, CancellationToken, ValueTask> reset,
    IReadOnlyList<SnapshotRegistration> snapshots
)
{
    public async ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        foreach (
            ISweepParticipant participant in scope.ServiceProvider.GetServices<ISweepParticipant>()
        )
        {
            await participant.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        await reset(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyDictionary<string, StateSnapshot>> CaptureAsync(
        CancellationToken cancellationToken
    )
    {
        var state = new SortedDictionary<string, StateSnapshot>(StringComparer.Ordinal);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        foreach (SnapshotRegistration snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StateSnapshot captured = await snapshot
                .Capture(scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);
            state.Add(
                snapshot.Name,
                captured
                    ?? throw new FaultSweepExecutionException(
                        $"Snapshot provider '{snapshot.Name}' returned null."
                    )
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ReadOnlyDictionary<string, StateSnapshot>(state);
    }
}
