using FaultSweep.Effects;
using FaultSweep.Snapshots;
using FaultSweep.Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Redis.Tests;

public sealed class VerificationRegressionFixture : IDisposable
{
    private readonly ServiceProvider services;
    private readonly MinimizeState state = new();
    private readonly bool baselineBroken;

    public VerificationRegressionFixture(bool baselineBroken)
    {
        this.baselineBroken = baselineBroken;
        var registrations = new ServiceCollection();
        registrations.AddFaultSweepXunit(Options);
        registrations.AddSingleton(state);
        registrations.AddScoped<MinimizeHandler>();
        services = registrations.BuildServiceProvider();
    }

    public bool Fixed { get; set; }
    public static XunitRegressionOptions Options { get; } =
        new()
        {
            FixtureTypeName = "global::FaultSweep.Redis.Tests.VerificationRegressionFixture",
            CollectionName = "Business verification",
            Namespace = "Generated",
        };

    public FaultSweepBuilder<MinimizeHandler, MinimizeRequest> CreateSweep(MinimizeRequest message)
    {
        return FaultSweep
            .For<MinimizeHandler>(services)
            .WithMessage(message)
            .ResetWith(
                (_, _) =>
                {
                    state.Value = 0;
                    return ValueTask.CompletedTask;
                }
            )
            .HandleWith(
                (_, request, token) =>
                    services
                        .GetRequiredService<IEffectInterceptor>()
                        .ExecuteAsync(
                            new EffectDescriptor("memory", "debit"),
                            _ =>
                            {
                                if (Fixed)
                                {
                                    state.Value = request.Amount;
                                }
                                else if (baselineBroken)
                                {
                                    state.Value = request.Amount + 1;
                                }
                                else
                                {
                                    lock (state)
                                    {
                                        state.Value += request.Amount;
                                    }
                                }

                                return ValueTask.CompletedTask;
                            },
                            token
                        )
            )
            .TrackState("balance", (_, _) => ValueTask.FromResult(StateSnapshot.From(state.Value)))
            .WithOptions(new FaultSweepOptions { CompareWithBaseline = false })
            .CheckInvariant(
                "exact payment amount",
                context => context.GetState<int>("balance") == context.Message.Amount
            );
    }

    public void Dispose() => services.Dispose();
}
