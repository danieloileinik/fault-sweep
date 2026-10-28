namespace FaultSweep;

public sealed class FaultSweepBuilder<THandler> where THandler : class
{
    private readonly IServiceProvider services;

    internal FaultSweepBuilder(IServiceProvider services)
    {
        this.services = services;
    }

    /// <summary>Reuses the same message on every delivery. Prefer an immutable message.</summary>
    public FaultSweepBuilder<THandler, TMessage> WithMessage<TMessage>(TMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new FaultSweepBuilder<THandler, TMessage>(services, message);
    }
}
