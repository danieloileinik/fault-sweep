using MediatR;

namespace FaultSweep.Postgres.Tests;

public sealed record MediatRSweepCommand(bool Idempotent = true, int Amount = 10)
    : IRequest<int>,
        IMediatRSweepRequest;
