namespace FaultSweep.Postgres.Tests;

public enum MessageFlow
{
    DualWrite,
    PublishBeforeCommit,
    LostEvent,
    NonIdempotentCounter,
    InboxBeforeWork,
    OutboxWithoutInbox,
    CorrectInboxOutbox,
    IdempotentUpsert
}
