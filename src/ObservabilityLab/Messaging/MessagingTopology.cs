using RabbitMQ.Client;

namespace ObservabilityLab.Messaging;

/// <summary>
/// Exchange and queues of the lab. A message the consumer rejects goes to the dead-letter queue
/// instead of being redelivered forever.
/// </summary>
public static class MessagingTopology
{
    public const string Exchange = "lab.messages";
    public const string Queue = "lab.messages";
    public const string RoutingKey = "lab.message";
    public const string DeadLetterExchange = "lab.messages.dlx";
    public const string DeadLetterQueue = "lab.messages.dead";

    /// <summary>Idempotent - safe on every start. Topology recovery re-declares it after a broker restart.</summary>
    public static async Task DeclareAsync(IConnection connection, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            DeadLetterExchange,
            ExchangeType.Fanout,
            durable: true,
            cancellationToken: ct
        );
        await channel.QueueDeclareAsync(
            DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            // Bounded, so a failing consumer cannot fill the broker's disk in the lab.
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-max-length"] = 10_000,
            },
            cancellationToken: ct
        );
        await channel.QueueBindAsync(
            DeadLetterQueue,
            DeadLetterExchange,
            "",
            cancellationToken: ct
        );

        await channel.ExchangeDeclareAsync(
            Exchange,
            ExchangeType.Direct,
            durable: true,
            cancellationToken: ct
        );
        await channel.QueueDeclareAsync(
            Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-dead-letter-exchange"] = DeadLetterExchange,
            },
            cancellationToken: ct
        );
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: ct);
    }
}
