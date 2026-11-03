namespace FaultSweep.Reporting;

public sealed record DeliveryAttempt(int Number, bool Succeeded, Exception? Error)
{
    public DeliveryIdentity Identity { get; init; } = new(1, Number);
}
