namespace FaultSweep.Postgres.Tests;

public sealed class AutomaticHandlerLifetime
{
    private int created;
    private int disposed;

    public int Created => created;
    public int Disposed => disposed;

    public void RecordCreation() => Interlocked.Increment(ref created);

    public void RecordDisposal() => Interlocked.Increment(ref disposed);
}
