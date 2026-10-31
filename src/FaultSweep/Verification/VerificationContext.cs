using System.Text.Json;
using FaultSweep.Reporting;
using FaultSweep.Snapshots;

namespace FaultSweep.Verification;

/// <summary>Read-only verification inputs after a delivery and its dependency injection scope have completed.</summary>
public sealed class VerificationContext<TMessage>
{
    internal VerificationContext(TMessage message, IServiceProvider services,
        IReadOnlyDictionary<string, StateSnapshot> state, FaultScenario? scenario,
        IReadOnlyList<DeliveryAttempt> attempts)
    {
        Message = message;
        Services = services;
        State = state;
        Scenario = scenario;
        Attempts = attempts;
    }

    public TMessage Message { get; }

    /// <summary>A verification scope, valid only during the callback. Use it for read-only queries.</summary>
    public IServiceProvider Services { get; }

    public IReadOnlyDictionary<string, StateSnapshot> State { get; }

    /// <summary>Null for the clean baseline.</summary>
    public FaultScenario? Scenario { get; }

    public bool IsBaseline => Scenario is null;

    public IReadOnlyList<DeliveryAttempt> Attempts { get; }

    public TState GetState<TState>(string name)
    {
        return State[name].Value.Deserialize<TState>()
            ?? throw new InvalidOperationException($"Snapshot '{name}' deserialized to null.");
    }
}
