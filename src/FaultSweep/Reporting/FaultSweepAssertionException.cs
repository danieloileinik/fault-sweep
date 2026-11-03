namespace FaultSweep.Reporting;

/// <summary>Fails any test framework that treats an unhandled exception as a test failure.</summary>
public sealed class FaultSweepAssertionException : Exception
{
    public FaultSweepAssertionException(string message)
        : base(message) { }

    internal FaultSweepAssertionException(FaultSweepResult result)
        : base((result ?? throw new ArgumentNullException(nameof(result))).ToString())
    {
        Result = result;
    }

    public FaultSweepResult? Result { get; }

    /// <summary>Expands an assertion failure without repeating or resetting the sweep.</summary>
    public string ToDetailedString() => Result?.ToDetailedString() ?? Message;
}
