using MediatR;
using Microsoft.EntityFrameworkCore.Storage;

namespace FaultSweep.Postgres.Tests;

public sealed class MediatRTransactionBehavior<TRequest, TResponse>(
    CounterContext context,
    MediatRPipelineProbe probe
) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken
    )
    {
        probe.Record(context, "transaction", cancellationToken);
        await using IDbContextTransaction transaction =
            await context.Database.BeginTransactionAsync(cancellationToken);
        TResponse response = await next();
        probe.Record(context, "save", cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        probe.Record(context, "commit", cancellationToken);
        return response;
    }
}
