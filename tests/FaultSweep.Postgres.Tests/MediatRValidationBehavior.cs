using MediatR;

namespace FaultSweep.Postgres.Tests;

public sealed class MediatRValidationBehavior<TRequest, TResponse>(
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
        probe.Record(context, "validate", cancellationToken);
        if (request is IMediatRSweepRequest { Amount: <= 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Amount must be positive.");
        }

        return await next();
    }
}
