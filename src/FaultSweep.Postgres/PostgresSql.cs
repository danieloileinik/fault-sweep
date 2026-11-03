namespace FaultSweep.Postgres;

internal static class PostgresSql
{
    public static string Qualified(string schema, string name) => $"{Quote(schema)}.{Quote(name)}";

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
