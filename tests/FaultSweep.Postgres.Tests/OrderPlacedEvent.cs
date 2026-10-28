namespace FaultSweep.Postgres.Tests;

public sealed record OrderPlacedEvent(int OrderId, decimal Amount);
