using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FaultSweep.Postgres.Tests;

public sealed class MediatRSweepHandler(CounterContext context, MediatRPipelineProbe probe)
    : IRequestHandler<MediatRSweepCommand, int>
{
    public async Task<int> Handle(MediatRSweepCommand request, CancellationToken cancellationToken)
    {
        probe.Record(context, "handler", cancellationToken);
        Counter counter = await context.Counters.SingleAsync(
            counter => counter.Id == 1,
            cancellationToken
        );
        counter.Value = request.Idempotent ? request.Amount : counter.Value + request.Amount;
        return counter.Value;
    }
}
