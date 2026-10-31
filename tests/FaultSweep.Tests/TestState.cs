using FaultSweep.Snapshots;

namespace FaultSweep.Tests;

public sealed class TestState
{
    public int Counter { get; set; }
    public bool Durable { get; set; }
    public bool Cache { get; set; }
    public bool Inbox { get; set; }
    public List<string> Events { get; } = [];
    public List<string> Outbox { get; } = [];
    public int Resets { get; private set; }
    public int HandlerInstances { get; set; }
    public int DisposedHandlers { get; set; }
    public List<int> DeliveryInstances { get; } = [];

    public int RegisterHandler()
    {
        lock (this)
        {
            return ++HandlerInstances;
        }
    }

    public void RecordDelivery(int instance)
    {
        lock (this)
        {
            DeliveryInstances.Add(instance);
        }
    }

    public void DisposeHandler()
    {
        lock (this)
        {
            DisposedHandlers++;
        }
    }

    public void Reset()
    {
        Counter = 0;
        Durable = false;
        Cache = false;
        Inbox = false;
        Events.Clear();
        Outbox.Clear();
        Resets++;
    }

    public StateSnapshot Snapshot() =>
        StateSnapshot.From(
            new
            {
                Counter,
                Durable,
                Cache,
                Inbox,
                Events = Events.ToArray(),
                Outbox = Outbox.ToArray(),
            }
        );
}
