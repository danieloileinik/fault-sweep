using FaultSweep.Reporting;

namespace FaultSweep.Execution;

internal sealed class SweepMinimizer<TMessage>(MessageMinimizer<TMessage>? minimizer, int maxRuns)
{
    public async Task<SweepMinimum<TMessage>?> MinimizeAsync(
        FaultSweepResult original,
        TMessage message,
        Func<TMessage, CancellationToken, Task<FaultSweepResult?>> evaluate,
        CancellationToken token
    )
    {
        // A fault scenario is meaningful only when the original and shrunken clean runs satisfy the business rules.
        if (original.BaselineVerification.HasViolations)
        {
            return null;
        }

        FaultScenarioResult? smallest = Smallest(original);
        DuplicateScenarioResult? smallestDuplicate = original.DuplicateViolations.FirstOrDefault();
        if (smallest is null && smallestDuplicate is null)
        {
            return null;
        }

        TMessage bestMessage = message;
        int evaluated = 0;
        bool exhausted = false;
        if (minimizer is not null)
        {
            long score = minimizer.Complexity(bestMessage);
            ArgumentOutOfRangeException.ThrowIfNegative(score);
            while (evaluated < maxRuns)
            {
                bool improved = false;
                // Only a strictly smaller candidate may replace the current message; cycles cannot keep the search alive.
                foreach (TMessage candidate in minimizer.Shrink(bestMessage))
                {
                    token.ThrowIfCancellationRequested();
                    if (evaluated >= maxRuns)
                    {
                        exhausted = true;
                        break;
                    }
                    evaluated++;
                    long candidateScore = minimizer.Complexity(candidate);
                    if (candidateScore < 0 || candidateScore >= score)
                    {
                        continue;
                    }

                    FaultSweepResult? candidateResult = await evaluate(candidate, token)
                        .ConfigureAwait(false);
                    FaultScenarioResult? candidateViolation = candidateResult is null
                        ? null
                        : Smallest(candidateResult);
                    DuplicateScenarioResult? candidateDuplicate =
                        candidateResult?.DuplicateViolations.FirstOrDefault();
                    if (candidateViolation is null && candidateDuplicate is null)
                    {
                        continue;
                    }

                    bestMessage = candidate;
                    score = candidateScore;
                    smallest = candidateViolation;
                    smallestDuplicate = candidateDuplicate;
                    improved = true;
                }
                if (!improved)
                {
                    break;
                }
            }
            exhausted |= evaluated >= maxRuns;
        }
        return new SweepMinimum<TMessage>(
            bestMessage,
            smallest,
            smallestDuplicate,
            evaluated,
            exhausted
        );
    }

    private static FaultScenarioResult? Smallest(FaultSweepResult result) =>
        result
            .Violations.OrderBy(item => item.Scenario.Point.Index)
            .ThenBy(item => item.Attempts.Count)
            .ThenBy(item => item.Scenario.Kind)
            .FirstOrDefault();
}
