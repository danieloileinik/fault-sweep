using System.Collections.Immutable;
using FaultSweep.Effects;

namespace FaultSweep.Postgres.Tests;

public sealed class PostgresTableOrderTests
{
    [Fact]
    public void Orders_a_dependency_diamond_before_restoring_children()
    {
        // Arrange
        PostgresTableBackup[] tables =
        [
            Table("leaf"),
            Table("right"),
            Table("left"),
            Table("root"),
        ];
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["leaf"] = ["left", "right"],
            ["right"] = ["root"],
            ["left"] = ["root"],
            ["root"] = [],
        };

        // Act
        ImmutableArray<PostgresTableBackup> ordered = PostgresTableOrder.Sort(tables, dependencies);

        // Assert
        Assert.Equal(["root", "right", "left", "leaf"], ordered.Select(table => table.Name));
        Assert.Equal(["leaf", "right", "left", "root"], tables.Select(table => table.Name));
        Assert.Equal(2, dependencies["leaf"].Count);
    }

    [Fact]
    public void Preserves_input_order_for_independent_tables()
    {
        // Arrange
        PostgresTableBackup[] tables = [Table("b"), Table("a")];
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["a"] = [],
            ["b"] = [],
        };

        // Act
        ImmutableArray<PostgresTableBackup> ordered = PostgresTableOrder.Sort(tables, dependencies);

        // Assert
        Assert.Equal(tables, ordered);
    }

    [Fact]
    public void Rejects_a_cycle_even_when_other_tables_can_be_restored()
    {
        // Arrange
        PostgresTableBackup[] tables = [Table("seed"), Table("a"), Table("b")];
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["seed"] = [],
            ["a"] = ["b"],
            ["b"] = ["a"],
        };

        // Act
        FaultSweepExecutionException error = Assert.Throws<FaultSweepExecutionException>(() =>
            PostgresTableOrder.Sort(tables, dependencies)
        );

        // Assert
        Assert.Contains("DEFERRABLE", error.Message);
    }

    private static PostgresTableBackup Table(string name) =>
        new PostgresTableBackup(name, "seed_" + name, ["id"]);
}
