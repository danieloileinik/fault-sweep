namespace FaultSweep.Postgres;

internal sealed record PostgresTriggerBackup(string Table, string Name, string Mode);
