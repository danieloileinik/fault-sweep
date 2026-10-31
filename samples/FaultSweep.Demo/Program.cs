using FaultSweep;
using FaultSweep.Demo;
using FaultSweep.Reporting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddFaultSweep();
services.AddSingleton<PaymentState>();
services.AddScoped<PaymentHandler>();
await using ServiceProvider provider = services.BuildServiceProvider();
PaymentState state = provider.GetRequiredService<PaymentState>();
bool[] modes = [false, true];
foreach (bool idempotent in modes)
{
    FaultSweepResult result = await global::FaultSweep
        .FaultSweep.For<PaymentHandler>(provider)
        .WithMessage(new Payment(42, 100, idempotent))
        .ResetWith(
            (_, _) =>
            {
                state.Reset();
                return ValueTask.CompletedTask;
            }
        )
        .TrackState("payments", (_, _) => ValueTask.FromResult(state.Snapshot()))
        .CheckInvariant(
            "one debit",
            context => context.State["payments"].Value.GetProperty("Debits").GetInt32() == 1
        )
        .CheckInvariant(
            "correct balance",
            context =>
                context.State["payments"].Value.GetProperty("Balance").GetInt32()
                == 1000 - context.Message.Amount
        )
        .WithDuplicateDeliveries()
        .RunAsync();
    Console.WriteLine(idempotent ? "Idempotent payment:" : "Non-idempotent payment:");
    Console.WriteLine(result);
    if (result.HasViolations == idempotent)
    {
        return 1;
    }
}

return 0;
