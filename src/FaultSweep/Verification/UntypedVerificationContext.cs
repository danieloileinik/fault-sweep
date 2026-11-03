using System.Text.Json;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Verification;

/// <summary>Read-only state shared by typed and message-independent invariants.</summary>
public class VerificationContext
{
    internal VerificationContext(
        IServiceProvider services,
        IReadOnlyDictionary<string, StateSnapshot> state,
        FaultScenario? scenario,
        IReadOnlyList<DeliveryAttempt> attempts,
        DuplicateDeliveryMode? duplicates
    )
    {
        Services = services;
        State = state;
        Scenario = scenario;
        Attempts = attempts;
        Duplicates = duplicates;
    }

    /// <summary>A verification scope, valid only during the callback. Use it for read-only queries.</summary>
    public IServiceProvider Services { get; }

    public IReadOnlyDictionary<string, StateSnapshot> State { get; }

    /// <summary>The injected fault, or null for baseline and duplicate deliveries. Use IsBaseline to distinguish them.</summary>
    public FaultScenario? Scenario { get; }

    public DuplicateDeliveryMode? Duplicates { get; }

    public bool IsBaseline => Scenario is null && Duplicates is null;

    public IReadOnlyList<DeliveryAttempt> Attempts { get; }

    public TState GetState<TState>(string name)
    {
        return State[name].Value.Deserialize<TState>()
            ?? throw new InvalidOperationException($"Snapshot '{name}' deserialized to null.");
    }
}
