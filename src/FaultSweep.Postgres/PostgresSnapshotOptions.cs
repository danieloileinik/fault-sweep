namespace FaultSweep.Postgres;

public sealed record PostgresSnapshotOptions
{
    public string Schema { get; init; } = "public";
    public required IReadOnlyCollection<string> Tables { get; init; }
    public IReadOnlyCollection<string> IgnoredColumns { get; init; } = [];

    internal PostgresSnapshotOptions ValidateAndCopy()
    {
        ValidateIdentifier(Schema);
        ArgumentNullException.ThrowIfNull(Tables);
        ArgumentNullException.ThrowIfNull(IgnoredColumns);
        if (Tables.Count == 0)
        {
            throw new ArgumentException("Select at least one PostgreSQL table.", nameof(Tables));
        }

        foreach (string identifier in Tables.Concat(IgnoredColumns))
        {
            ValidateIdentifier(identifier);
        }

        return this with
        {
            Tables = Array.AsReadOnly(
                Tables.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
            ),
            IgnoredColumns = Array.AsReadOnly(
                IgnoredColumns.Distinct(StringComparer.Ordinal).ToArray()
            ),
        };
    }

    private static void ValidateIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Contains('\0'))
        {
            throw new ArgumentException(
                "PostgreSQL identifiers cannot contain a null character.",
                nameof(identifier)
            );
        }
    }
}
