using System.Collections.Immutable;
using FaultSweep.Effects;

namespace FaultSweep.Postgres;

internal static class PostgresTableOrder
{
    public static ImmutableArray<PostgresTableBackup> Sort(
        IReadOnlyList<PostgresTableBackup> tables,
        IReadOnlyDictionary<string, HashSet<string>> dependencies
    )
    {
        List<PostgresTableBackup> remaining = [.. tables];
        List<PostgresTableBackup> ordered = [];
        var restored = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            PostgresTableBackup? next = remaining.FirstOrDefault(table =>
                dependencies[table.Name].All(restored.Contains)
            );
            if (next is null)
            {
                throw new FaultSweepExecutionException(
                    "Automatic PostgreSQL restoration requires cyclic foreign keys to be DEFERRABLE."
                );
            }

            remaining.Remove(next);
            restored.Add(next.Name);
            ordered.Add(next);
        }

        return ordered.ToImmutableArray();
    }
}
