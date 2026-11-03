namespace FaultSweep.Postgres;

internal sealed record PostgresSequenceBackup(string Name, long Value, bool IsCalled);
