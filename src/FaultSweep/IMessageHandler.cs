namespace FaultSweep;

public interface IMessageHandler<in TMessage>
{
    ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken);
}
