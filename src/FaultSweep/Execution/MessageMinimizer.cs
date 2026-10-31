namespace FaultSweep.Execution;

internal sealed record MessageMinimizer<T>(Func<T, IEnumerable<T>> Shrink, Func<T, long> Complexity);
