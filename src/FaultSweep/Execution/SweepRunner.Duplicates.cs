using FaultSweep.Effects;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;
using FaultSweep.Verification;

namespace FaultSweep.Execution;

internal sealed partial class SweepRunner<TMessage>
{
    private async Task<DuplicateScenarioResult> RunDuplicatesAsync(
        DuplicateDeliveryMode mode,
        IReadOnlyDictionary<string, StateSnapshot> baseline,
        IReadOnlyDictionary<string, StateSnapshot> expected,
        TMessage runMessage,
        CancellationToken token
    )
    {
        await ResetAsync(token).ConfigureAwait(false);
        (List<DeliveryAttempt> Attempts, List<EffectTraceEntry> Trace)[] deliveries;
        if (mode == DuplicateDeliveryMode.Sequential)
        {
            deliveries =
            [
                await DeliverDuplicateAsync(0, runMessage, token).ConfigureAwait(false),
                await DeliverDuplicateAsync(1, runMessage, token).ConfigureAwait(false),
            ];
        }
        else
        {
            var ready = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            int waiting = 0;
            Task<(List<DeliveryAttempt>, List<EffectTraceEntry>)> Launch(int delivery) =>
                Task.Run(
                    async () =>
                    {
                        if (Interlocked.Increment(ref waiting) == 2)
                        {
                            ready.SetResult();
                        }

                        await start.Task.WaitAsync(token).ConfigureAwait(false);
                        return await DeliverDuplicateAsync(delivery, runMessage, token)
                            .ConfigureAwait(false);
                    },
                    token
                );

            Task<(List<DeliveryAttempt>, List<EffectTraceEntry>)>[] tasks = [Launch(0), Launch(1)];
            try
            {
                await ready.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                start.TrySetResult();
                try
                {
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch
                {
                    // Observe both deliveries before propagating cancellation from the start gate.
                }

                throw;
            }

            start.TrySetResult();

            // Wait for both deliveries before snapshots, reset, or propagating an infrastructure failure.
            deliveries = await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        DeliveryAttempt[] attempts = deliveries
            .SelectMany(delivery => delivery.Attempts)
            .OrderBy(attempt => attempt.Number)
            .ToArray();
        EffectTraceEntry[] trace = deliveries
            .SelectMany(delivery => delivery.Trace)
            .OrderBy(entry => entry.Attempt)
            .ThenBy(entry => entry.Point.Index)
            .ToArray();
        IReadOnlyDictionary<string, StateSnapshot> state = await CaptureAsync(token)
            .ConfigureAwait(false);
        VerificationResult verification = await VerifyAsync(
                runMessage,
                state,
                expected,
                baseline,
                null,
                Array.AsReadOnly(attempts),
                token,
                mode
            )
            .ConfigureAwait(false);
        return new DuplicateScenarioResult(
            mode,
            attempts,
            trace,
            state,
            verification,
            deliveries.All(delivery => delivery.Attempts[^1].Succeeded)
        );
    }

    private async Task<(
        List<DeliveryAttempt> Attempts,
        List<EffectTraceEntry> Trace
    )> DeliverDuplicateAsync(int delivery, TMessage runMessage, CancellationToken token)
    {
        List<DeliveryAttempt> attempts = [];
        List<EffectTraceEntry> trace = [];
        for (int retry = 0; retry <= options.MaxRedeliveryAttempts; retry++)
        {
            int number = retry * 2 + delivery + 1;
            var session = new EffectSession(number, options.MaxEffects);
            DeliveryAttempt attempt = await DeliverAsync(session, number, runMessage, token)
                .ConfigureAwait(false);
            attempts.Add(attempt);
            trace.AddRange(session.Trace);
            if (attempt.Succeeded)
            {
                break;
            }
        }

        return (attempts, trace);
    }
}
