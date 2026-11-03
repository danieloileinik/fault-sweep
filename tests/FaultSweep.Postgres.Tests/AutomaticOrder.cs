namespace FaultSweep.Postgres.Tests;

public sealed record AutomaticOrder(bool Idempotent = true, bool Fail = false);
