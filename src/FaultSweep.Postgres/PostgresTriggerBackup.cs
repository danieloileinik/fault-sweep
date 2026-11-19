namespace FaultSweep.Postgres;

internal sealed record PostgresTriggerBackup(string Schema, string Table, string Name, string Mode);
