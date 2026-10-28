namespace FaultSweep.Tests;

public static class ZooBehaviors
{
    public static TestBehavior Create(ZooCase example) => new TestBehavior(async (state, effects, token) =>
    {
        switch (example)
        {
            case ZooCase.NonIdempotentCounter:
                await SweepTestEnvironment.Apply(effects, "increment", () => state.Counter++, token);
                break;
            case ZooCase.DualWrite:
                if (!state.Durable)
                {
                    await SweepTestEnvironment.Apply(effects, "insert-order", () => state.Durable = true, token);
                    await SweepTestEnvironment.Apply(effects, "update-balance", () => state.Counter += 10, token);
                }

                break;
            case ZooCase.PublishBeforeCommit:
                if (!state.Durable)
                {
                    await SweepTestEnvironment.Apply(effects, "publish", () => state.Events.Add("placed"), token);
                    await SweepTestEnvironment.Apply(effects, "commit", () => state.Durable = true, token);
                }

                break;
            case ZooCase.CacheBeforeCommit:
                if (!state.Cache)
                {
                    await SweepTestEnvironment.Apply(effects, "cache", () => state.Cache = true, token);
                    await SweepTestEnvironment.Apply(effects, "commit", () => state.Durable = true, token);
                }

                break;
            case ZooCase.LostEvent:
                if (!state.Durable)
                {
                    await SweepTestEnvironment.Apply(effects, "commit", () => state.Durable = true, token);
                    await SweepTestEnvironment.Apply(effects, "publish", () => state.Events.Add("placed"), token);
                }

                break;
            case ZooCase.InboxBeforeWork:
                if (!state.Inbox)
                {
                    await SweepTestEnvironment.Apply(effects, "inbox", () => state.Inbox = true, token);
                    await SweepTestEnvironment.Apply(effects, "work", () => state.Counter += 10, token);
                }

                break;
            case ZooCase.OutboxWithoutInbox:
                await SweepTestEnvironment.Apply(effects, "commit", () =>
                {
                    state.Counter += 10;
                    state.Outbox.Add("placed");
                }, token);
                break;
            case ZooCase.AtomicInboxOutbox:
                if (!state.Inbox)
                {
                    await SweepTestEnvironment.Apply(effects, "commit", () =>
                    {
                        state.Counter += 10;
                        state.Outbox.Add("placed");
                        state.Inbox = true;
                    }, token);
                }

                break;
            case ZooCase.IdempotentUpsert:
                await SweepTestEnvironment.Apply(effects, "upsert", () => state.Counter = 10, token);
                await SweepTestEnvironment.Apply(effects, "cache", () => state.Cache = true, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(example));
        }
    });
}
