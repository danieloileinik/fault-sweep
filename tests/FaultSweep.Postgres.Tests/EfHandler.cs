using Microsoft.EntityFrameworkCore;

namespace FaultSweep.Postgres.Tests;

public sealed class EfHandler(CounterContext context) : IMessageHandler<PostgresMessage>
{
    public async ValueTask HandleAsync(PostgresMessage message, CancellationToken cancellationToken)
    {
        if (message.Async)
        {
            List<Counter> counters = await context.Counters.OrderBy(counter => counter.Id).ToListAsync(cancellationToken);
            foreach (Counter counter in counters)
            {
                counter.Value = message.Idempotent ? 10 : counter.Value + 10;
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            var counters = context.Counters.OrderBy(counter => counter.Id).ToList();
            foreach (Counter counter in counters)
            {
                counter.Value = message.Idempotent ? 10 : counter.Value + 10;
            }

            context.SaveChanges();
        }
    }
}
