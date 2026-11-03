namespace FaultSweep.Reporting;

/// <summary>Identifies a logical delivery and its one-based attempt independently of report ordering.</summary>
public sealed record DeliveryIdentity(int Delivery, int Attempt);
