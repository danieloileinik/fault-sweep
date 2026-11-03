using FaultSweep.Reporting;

namespace FaultSweep.Execution;

internal static class DeliveryScheduler
{
    public static async Task<T[]> RunAsync<T>(
        IReadOnlyList<Func<CancellationToken, Task<T>>> deliveries,
        DuplicateDeliveryMode mode,
        CancellationToken token
    )
    {
        if (mode == DuplicateDeliveryMode.Sequential)
        {
            List<T> results = [];
            foreach (Func<CancellationToken, Task<T>> delivery in deliveries)
            {
                token.ThrowIfCancellationRequested();
                results.Add(await delivery(token).ConfigureAwait(false));
            }

            return [.. results];
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waiting = 0;
        Task<T> Launch(Func<CancellationToken, Task<T>> delivery) =>
            Task.Run(
                async () =>
                {
                    if (Interlocked.Increment(ref waiting) == deliveries.Count)
                    {
                        start.TrySetResult();
                    }

                    await start.Task.WaitAsync(token).ConfigureAwait(false);
                    return await delivery(token).ConfigureAwait(false);
                },
                token
            );

        Task<T>[] tasks = deliveries.Select(Launch).ToArray();
        // Observe every delivery before snapshots, reset, or propagating failures.
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
