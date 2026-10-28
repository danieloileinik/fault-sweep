namespace FaultSweep.Reporting;

/// <summary>Fails any test framework that treats an unhandled exception as a test failure.</summary>
public sealed class FaultSweepAssertionException(string message) : Exception(message);
