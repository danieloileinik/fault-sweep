namespace FaultSweep.Tests;

public enum ZooCase
{
    NonIdempotentCounter,
    DualWrite,
    PublishBeforeCommit,
    CacheBeforeCommit,
    LostEvent,
    InboxBeforeWork,
    OutboxWithoutInbox,
    AtomicInboxOutbox,
    IdempotentUpsert
}
