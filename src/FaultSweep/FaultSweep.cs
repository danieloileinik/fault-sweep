using Microsoft.Extensions.Hosting;

namespace FaultSweep;

public static class FaultSweep
{
    public static FaultSweepBuilder<THandler> For<THandler>(IHost host) where THandler : class
    {
        ArgumentNullException.ThrowIfNull(host);
        return For<THandler>(host.Services);
    }

    public static FaultSweepBuilder<THandler> For<THandler>(IServiceProvider services) where THandler : class
    {
        ArgumentNullException.ThrowIfNull(services);
        return new FaultSweepBuilder<THandler>(services);
    }
}
