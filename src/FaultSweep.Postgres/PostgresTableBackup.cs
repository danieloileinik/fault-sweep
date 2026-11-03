namespace FaultSweep.Postgres;

internal sealed record PostgresTableBackup(string Name, string TemporaryName, string[] Columns);
