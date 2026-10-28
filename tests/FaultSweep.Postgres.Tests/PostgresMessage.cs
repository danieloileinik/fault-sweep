namespace FaultSweep.Postgres.Tests;

public sealed record PostgresMessage(bool Async, bool Transactional, bool Idempotent, CommandApi Api = CommandApi.NonQuery);
