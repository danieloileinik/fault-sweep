using FaultSweep.Snapshots;
using Microsoft.Extensions.DependencyInjection;

namespace FaultSweep.Kafka;

public static class FaultSweepKafkaBuilderExtensions
{
    /// <summary>Compares logical messages without broker offsets or timestamps. Payload array order is preserved.</summary>
    public static FaultSweepBuilder<THandler, TMessage> TrackProduced<THandler, TMessage>(
        this FaultSweepBuilder<THandler, TMessage> builder, ArrayComparison comparison = ArrayComparison.Multiset,
        SnapshotComparisonOptions? normalization = null, string name = "produced") where THandler : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        SnapshotComparisonOptions options = (normalization ?? new SnapshotComparisonOptions()) with { ArrayComparison = comparison };
        return builder.ValidateWith(services =>
        {
            if (services.GetService<ProducedSnapshotProvider>() is null)
            {
                throw new InvalidOperationException("TrackProduced requires services.AddFaultSweepKafka<TKey, TValue>() in the test configuration.");
            }
        }).TrackSnapshot<ProducedSnapshotProvider>(name, options);
    }
}
