using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Execution;

internal static class SweepInterceptors
{
    private static readonly ConditionalWeakTable<IServiceProvider, EffectInterceptor> Interceptors =
        new();

    public static EffectInterceptor For(IServiceProvider services) =>
        services.GetService<EffectInterceptor>()
        ?? Interceptors.GetValue(services, _ => new EffectInterceptor());
}
