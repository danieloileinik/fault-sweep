namespace FaultSweep.Postgres.Tests;

public static class WorkloadSql
{
    public static string Update(PostgresMessage message)
    {
        string value = message.Idempotent ? "@amount" : "value + @amount";
        string returning = message.Api == CommandApi.NonQuery ? string.Empty : " RETURNING value";
        return $"UPDATE counters SET value = {value} WHERE id = @id{returning}";
    }
}
