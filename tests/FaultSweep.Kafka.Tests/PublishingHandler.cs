using Confluent.Kafka;

namespace FaultSweep.Kafka.Tests;

public sealed class PublishingHandler(IProducer<string, EventPayload> producer) : IMessageHandler<PublishRequest>
{
    public async ValueTask HandleAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        byte[] header = [1, 2];
        var payload = new EventPayload();
        var message = new Message<string, EventPayload>
        {
            Key = "order-1",
            Value = payload,
            Headers = new Headers { { "trace", header } }
        };
        var destination = new TopicPartition("orders", request.ExplicitPartition ? new Partition(2) : Partition.Any);
        if (request.Async)
        {
            DeliveryResult<string, EventPayload> result = request.ExplicitPartition
                ? await producer.ProduceAsync(destination, message, cancellationToken)
                : await producer.ProduceAsync("orders", message, cancellationToken);
            if (result.Status != PersistenceStatus.Persisted || result.Offset.Value < 0)
            {
                throw new InvalidOperationException("The capture producer did not acknowledge the message.");
            }
        }
        else
        {
            bool acknowledged = false;
            void Delivered(DeliveryReport<string, EventPayload> report)
            {
                acknowledged = !report.Error.IsError && report.Status == PersistenceStatus.Persisted;
            }

            if (request.ExplicitPartition)
            {
                producer.Produce(destination, message, Delivered);
            }
            else
            {
                producer.Produce("orders", message, Delivered);
            }

            if (!acknowledged)
            {
                throw new InvalidOperationException("The synchronous delivery callback did not run.");
            }
        }

        if (request.Mutate)
        {
            payload.Amount = 99;
            header[0] = 99;
        }
    }
}
