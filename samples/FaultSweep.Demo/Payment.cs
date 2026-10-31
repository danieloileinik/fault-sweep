namespace FaultSweep.Demo;

public sealed record Payment(int PaymentId, int Amount, bool Idempotent);
