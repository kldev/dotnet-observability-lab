using RabbitMQ.Client;

namespace ObservabilityLab.Messaging;

/// <summary>
/// Exchanges and queues of the lab. A message the consumer rejects goes to the dead-letter queue
/// instead of being redelivered forever.
/// <para>
/// Emails use a topic exchange (<c>x.</c> = exchange, <c>q.</c> = queue): the routing key is
/// <c>&lt;department&gt;.&lt;priority&gt;</c> (e.g. <c>sales.urgent</c>) and every queue picks what it needs
/// with a binding pattern - <c>*</c> matches one word, <c>#</c> zero or more.
/// </para>
/// </summary>
public static class MessagingTopology
{
    public const string Exchange = "lab.messages";
    public const string Queue = "lab.messages";
    public const string RoutingKey = "lab.message";
    public const string DeadLetterExchange = "lab.messages.dlx";
    public const string DeadLetterQueue = "lab.messages.dead";

    public const string EmailsExchange = "x.emails";

    /// <summary>Email queues and their binding patterns on <see cref="EmailsExchange"/>. One message can land in several.</summary>
    public static readonly IReadOnlyList<(string Queue, string Binding)> EmailQueues =
    [
        ("q.emails.recruitment", "recruitment.*"),
        ("q.emails.sales", "sales.*"),
        ("q.emails.support", "support.*"),
        ("q.emails.urgent", "*.urgent"), // urgent emails of every department
        ("q.emails.audit", "#"), // everything
    ];

    /// <summary>Every queue the consumer reads.</summary>
    public static IEnumerable<string> ConsumedQueues =>
        [Queue, .. EmailQueues.Select(q => q.Queue)];

    /// <summary>The email queues whose binding matches <paramref name="routingKey"/> - the same rule the broker applies.</summary>
    public static IReadOnlyList<string> EmailQueuesFor(string routingKey) =>
        [.. EmailQueues.Where(q => TopicMatches(q.Binding, routingKey)).Select(q => q.Queue)];

    /// <summary>AMQP topic matching: words split by '.', <c>*</c> = exactly one word, <c>#</c> = zero or more.</summary>
    public static bool TopicMatches(string pattern, string routingKey) =>
        Matches(pattern.Split('.'), routingKey.Split('.'));

    private static bool Matches(ReadOnlySpan<string> pattern, ReadOnlySpan<string> words) =>
        pattern switch
        {
            [] => words.IsEmpty,
            ["#", .. var rest] => Matches(rest, words)
                || (!words.IsEmpty && Matches(pattern, words[1..])),
            [var head, .. var rest] => !words.IsEmpty
                && (head == "*" || head == words[0])
                && Matches(rest, words[1..]),
        };

    public static string EmailRoutingKey(EmailDepartment department, EmailPriority priority) =>
        $"{department.ToString().ToLowerInvariant()}.{priority.ToString().ToLowerInvariant()}";

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
        await DeclareQueueAsync(channel, Queue, ct);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: ct);

        await channel.ExchangeDeclareAsync(
            EmailsExchange,
            ExchangeType.Topic,
            durable: true,
            cancellationToken: ct
        );
        foreach (var (queue, binding) in EmailQueues)
        {
            await DeclareQueueAsync(channel, queue, ct);
            await channel.QueueBindAsync(queue, EmailsExchange, binding, cancellationToken: ct);
        }
    }

    private static Task<QueueDeclareOk> DeclareQueueAsync(
        IChannel channel,
        string queue,
        CancellationToken ct
    ) =>
        channel.QueueDeclareAsync(
            queue,
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
}
