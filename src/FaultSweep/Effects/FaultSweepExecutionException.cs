namespace FaultSweep.Effects;

/// <summary>A baseline or instrumentation failure that prevents a valid sweep.</summary>
public sealed class FaultSweepExecutionException : Exception
{
    public FaultSweepExecutionException(string message) : base(message)
    {
    }

    public FaultSweepExecutionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
