namespace FaultSweep.Verification;

internal sealed record InvariantRegistration<TMessage>(string Name,
    Func<VerificationContext<TMessage>, CancellationToken, ValueTask<bool>> Check, string FailureMessage);
