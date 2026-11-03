using MediatR;

namespace FaultSweep.Postgres.Tests;

public sealed record MediatRVoidCommand(int Amount = 10) : IRequest, IMediatRSweepRequest;
