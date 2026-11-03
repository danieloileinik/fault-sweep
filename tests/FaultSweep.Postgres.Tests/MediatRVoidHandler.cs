using MediatR;
using Microsoft.EntityFrameworkCore;

namespace FaultSweep.Postgres.Tests;

public sealed class MediatRVoidHandler(CounterContext context, MediatRPipelineProbe probe)
    : IRequestHandler<MediatRVoidCommand>
{
    public async Task Handle(MediatRVoidCommand request, CancellationToken cancellationToken)
    {
        probe.Record(context, "handler", cancellationToken);
        Counter counter = await context.Counters.SingleAsync(
            counter => counter.Id == 1,
            cancellationToken
        );
        counter.Value = request.Amount;
    }
}
