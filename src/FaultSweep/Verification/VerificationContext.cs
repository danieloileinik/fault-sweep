using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Verification;

/// <summary>Read-only verification inputs including the current message.</summary>
public sealed class VerificationContext<TMessage> : VerificationContext
{
    internal VerificationContext(
        TMessage message,
        IServiceProvider services,
        IReadOnlyDictionary<string, StateSnapshot> state,
        FaultScenario? scenario,
        IReadOnlyList<DeliveryAttempt> attempts,
        DuplicateDeliveryMode? duplicates = null
    )
        : base(services, state, scenario, attempts, duplicates)
    {
        Message = message;
    }

    public TMessage Message { get; }
}
