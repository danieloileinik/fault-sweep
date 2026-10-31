using FaultSweep.Snapshots;

namespace FaultSweep.Demo;

public sealed class PaymentState
{
    public int Balance { get; private set; } = 1000;
    public int Debits { get; private set; }
    private readonly HashSet<int> inbox = [];

    public void Reset()
    {
        Balance = 1000;
        Debits = 0;
        inbox.Clear();
    }

    public void Apply(Payment payment)
    {
        lock (inbox)
        {
            if (payment.Idempotent && !inbox.Add(payment.PaymentId))
            {
                return;
            }

            Balance -= payment.Amount;
            Debits++;
        }
    }

    public StateSnapshot Snapshot() => StateSnapshot.From(new { Balance, Debits });
}
