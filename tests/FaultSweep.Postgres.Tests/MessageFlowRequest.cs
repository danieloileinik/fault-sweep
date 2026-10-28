namespace FaultSweep.Postgres.Tests;

public sealed record MessageFlowRequest(MessageFlow Flow, int OrderId = 1, decimal Amount = 100);
