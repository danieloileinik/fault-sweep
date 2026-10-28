using FaultSweep.Effects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FaultSweep.Tests;

public sealed class SweepTestEnvironment : IDisposable
{
    public SweepTestEnvironment(TestBehavior behavior)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddFaultSweep();
        builder.Services.AddSingleton(State);
        builder.Services.AddSingleton(behavior);
        builder.Services.AddScoped<TestHandler>();
        builder.Services.AddScoped<TestSnapshotProvider>();
        Host = builder.Build();
    }

    public TestState State { get; } = new TestState();

    public IHost Host { get; }

    public FaultSweepBuilder<TestHandler, TestMessage> CreateSweep()
    {
        return FaultSweep.For<TestHandler>(Host)
            .WithMessage(new TestMessage())
            .ResetWith((services, _) =>
            {
                services.GetRequiredService<TestState>().Reset();
                return ValueTask.CompletedTask;
            })
            .TrackSnapshot<TestSnapshotProvider>("state");
    }

    public void Dispose() => Host.Dispose();

    public static ValueTask Apply(IEffectInterceptor effects, string name, Action operation,
        CancellationToken token = default)
    {
        return effects.ExecuteAsync(new EffectDescriptor("memory", name), _ =>
        {
            operation();
            return ValueTask.CompletedTask;
        }, token);
    }
}
