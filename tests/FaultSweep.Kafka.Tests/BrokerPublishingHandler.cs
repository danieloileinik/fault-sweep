using Confluent.Kafka;
using FaultSweep.Effects;

namespace FaultSweep.Kafka.Tests;

public sealed class BrokerPublishingHandler(
    IProducer<string, string> producer,
    IEffectInterceptor interceptor
) : IMessageHandler<string>
{
    public async ValueTask HandleAsync(string topic, CancellationToken cancellationToken)
    {
        await interceptor.ExecuteAsync(
            new EffectDescriptor("kafka", "Produce", "payments"),
            async token =>
            {
                await producer.ProduceAsync(
                    topic,
                    new Message<string, string> { Key = "payment-42", Value = "paid" },
                    token
                );
            },
            cancellationToken
        );
    }
}
