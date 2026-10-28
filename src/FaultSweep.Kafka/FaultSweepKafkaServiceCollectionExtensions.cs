using Confluent.Kafka;
using FaultSweep.Effects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FaultSweep.Kafka;

public static class FaultSweepKafkaServiceCollectionExtensions
{
    /// <summary>Replaces the producer with a capture-only implementation. No Kafka client or network connection is created.</summary>
    public static IServiceCollection AddFaultSweepKafka<TKey, TValue>(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFaultSweep();
        services.TryAddSingleton<KafkaMessageCapture>();
        services.TryAddSingleton<ProducedSnapshotProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEffectInstrumentation, KafkaInstrumentation>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISweepParticipant, KafkaSweepParticipant>());
        services.Replace(ServiceDescriptor.Scoped<IProducer<TKey, TValue>, CapturingProducer<TKey, TValue>>());
        return services;
    }
}
