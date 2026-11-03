using System.Collections.Concurrent;

namespace FaultSweep.Postgres.Tests;

public sealed class MediatRPipelineProbe
{
    public ConcurrentDictionary<CounterContext, ConcurrentQueue<string>> Deliveries { get; } =
        new();
    public ConcurrentBag<CancellationToken> Tokens { get; } = [];

    public void Record(CounterContext context, string stage, CancellationToken token)
    {
        Deliveries.GetOrAdd(context, _ => new ConcurrentQueue<string>()).Enqueue(stage);
        Tokens.Add(token);
    }
}
