using FaultSweep.Effects;
using FaultSweep.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FaultSweep;

public static class FaultSweepServiceCollectionExtensions
{
    public static IServiceCollection AddFaultSweep(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<EffectInterceptor>();
        services.TryAddSingleton<IEffectInterceptor>(provider => provider.GetRequiredService<EffectInterceptor>());
        return services;
    }
}
