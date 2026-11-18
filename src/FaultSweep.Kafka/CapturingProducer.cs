using Confluent.Kafka;
using FaultSweep.Effects;

namespace FaultSweep.Kafka;

/// <summary>Captures produces immediately. Synchronous delivery callbacks run inline; Kafka transactions are unsupported.</summary>
internal sealed class CapturingProducer<TKey, TValue>(KafkaMessageCapture capture)
    : IProducer<TKey, TValue>
{
    private bool disposed;

    public string Name => "FaultSweep.CaptureProducer";
    public Handle Handle =>
        throw new NotSupportedException("The capture producer has no native Kafka handle.");

    public Task<DeliveryResult<TKey, TValue>> ProduceAsync(
        string topic,
        Message<TKey, TValue> message,
        CancellationToken cancellationToken = default
    ) => ProduceAsync(new TopicPartition(topic, Partition.Any), message, cancellationToken);

    public async Task<DeliveryResult<TKey, TValue>> ProduceAsync(
        TopicPartition topicPartition,
        Message<TKey, TValue> message,
        CancellationToken cancellationToken = default
    )
    {
        Validate(topicPartition, message);
        KafkaCaptureSession session = capture.Session;
        return await session
            .Interceptor.ExecuteAsync(
                Describe(topicPartition),
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(Capture(topicPartition, message));
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public void Produce(
        string topic,
        Message<TKey, TValue> message,
        Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null
    ) => Produce(new TopicPartition(topic, Partition.Any), message, deliveryHandler);

    public void Produce(
        TopicPartition topicPartition,
        Message<TKey, TValue> message,
        Action<DeliveryReport<TKey, TValue>>? deliveryHandler = null
    )
    {
        Validate(topicPartition, message);
        DeliveryResult<TKey, TValue> result = capture.Session.Interceptor.Execute(
            Describe(topicPartition),
            () => Capture(topicPartition, message)
        );
        deliveryHandler?.Invoke(
            new DeliveryReport<TKey, TValue>
            {
                Topic = result.Topic,
                Partition = result.Partition,
                Offset = result.Offset,
                Message = message,
                Status = PersistenceStatus.Persisted,
                Error = new Error(ErrorCode.NoError),
            }
        );
    }

    public int Poll(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return 0;
    }

    public int Flush(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return 0;
    }

    public void Flush(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => disposed = true;

    public int AddBrokers(string brokers) =>
        throw new NotSupportedException("The capture producer does not connect to brokers.");

    public void SetSaslCredentials(string username, string password) =>
        throw new NotSupportedException("The capture producer does not authenticate with brokers.");

    public void InitTransactions(TimeSpan timeout) => RejectTransactions();

    public void BeginTransaction() => RejectTransactions();

    public void CommitTransaction(TimeSpan timeout) => RejectTransactions();

    public void CommitTransaction() => RejectTransactions();

    public void AbortTransaction(TimeSpan timeout) => RejectTransactions();

    public void AbortTransaction() => RejectTransactions();

    public void SendOffsetsToTransaction(
        IEnumerable<TopicPartitionOffset> offsets,
        IConsumerGroupMetadata groupMetadata,
        TimeSpan timeout
    ) => RejectTransactions();

    private void RejectTransactions()
    {
        capture.Session.Interceptor.ReportInstrumentationFailure(
            "Kafka transactions are not supported by the M1 capture producer."
        );
    }

    private DeliveryResult<TKey, TValue> Capture(
        TopicPartition destination,
        Message<TKey, TValue> message
    )
    {
        Partition assignedPartition = destination.Partition.IsSpecial
            ? new Partition(0)
            : destination.Partition;
        long offset = capture.Add(destination, assignedPartition, message);
        return new DeliveryResult<TKey, TValue>
        {
            Topic = destination.Topic,
            Partition = assignedPartition,
            Offset = new Offset(offset),
            Message = message,
            Status = PersistenceStatus.Persisted,
        };
    }

    private void Validate(TopicPartition destination, Message<TKey, TValue> message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination.Topic);
        ArgumentNullException.ThrowIfNull(message);
    }

    private static EffectDescriptor Describe(TopicPartition destination) =>
        new("kafka", "Produce", $"{destination.Topic}[{destination.Partition.Value}]");
}
