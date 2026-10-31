namespace FaultSweep.Verification;

public sealed record InvariantViolation(string Name, string Message, Exception? Error = null);
