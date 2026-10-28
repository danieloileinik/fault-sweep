namespace FaultSweep.Reporting;

[Flags]
public enum ViolationKind
{
    None = 0,
    StateMismatch = 1,
    RedeliveryLimitExceeded = 2,
    FaultNotReached = 4
}
