namespace FaultSweep.Reporting;

public sealed record DeliveryAttempt(int Number, bool Succeeded, Exception? Error);
