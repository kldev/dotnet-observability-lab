using System.Diagnostics;
using Mediator;
using RabbitMQ.Client.Exceptions;

namespace ObservabilityLab.Messaging;

// Messages ------------------------------------------------------------------

public sealed record PublishMessage(string Text, int ProcessingMs, bool Fail)
    : ICommand<LabMessage>;

/// <summary>Publishes <paramref name="Count"/> messages from <paramref name="Parallelism"/> concurrent tasks.</summary>
public sealed record PublishBurst(int Count, int Parallelism, int ProcessingMs)
    : ICommand<BurstResult>;

public sealed record BurstResult(int Published, int Failed, TimeSpan Duration);

public sealed record GetQueueStatus : IQuery<QueueStatus>;

public sealed record QueueStatus(uint Ready, uint Consumers, uint DeadLettered);

public sealed record GetReceivedMessages(int Limit) : IQuery<IReadOnlyList<ReceivedMessage>>;

/// <summary>Publishes to the <c>x.emails</c> topic exchange with routing key <c>&lt;department&gt;.&lt;priority&gt;</c>.</summary>
public sealed record PublishEmail(
    EmailDepartment Department,
    EmailPriority Priority,
    string Subject,
    int ProcessingMs,
    bool Fail
) : ICommand<PublishedEmail>;

public sealed record PublishedEmail(LabMessage Message, string RoutingKey);

public sealed record GetEmailQueues : IQuery<IReadOnlyList<EmailQueueStatus>>;

public sealed record EmailQueueStatus(string Queue, string Binding, uint Ready, uint Consumers);

// Handlers ------------------------------------------------------------------

public sealed class PublishMessageHandler(MessagePublisher publisher)
    : ICommandHandler<PublishMessage, LabMessage>
{
    public async ValueTask<LabMessage> Handle(PublishMessage command, CancellationToken ct) =>
        await publisher.PublishAsync(command.Text, command.ProcessingMs, command.Fail, ct);
}

public sealed class PublishBurstHandler(
    MessagePublisher publisher,
    ILogger<PublishBurstHandler> logger
) : ICommandHandler<PublishBurst, BurstResult>
{
    public async ValueTask<BurstResult> Handle(PublishBurst command, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var published = 0;
        var failed = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(1, command.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = command.Parallelism,
                CancellationToken = ct,
            },
            async (i, token) =>
            {
                try
                {
                    await publisher.PublishAsync(
                        $"Burst message {i}/{command.Count}",
                        command.ProcessingMs,
                        fail: false,
                        token
                    );
                    Interlocked.Increment(ref published);
                }
                catch (PublishException)
                {
                    // The broker nacked or returned this one message - count it and keep going.
                    Interlocked.Increment(ref failed);
                }
            }
        );

        var result = new BurstResult(published, failed, Stopwatch.GetElapsedTime(started));
        logger.LogInformation(
            "Burst of {Count} messages from {Parallelism} tasks: {Published} published, {Failed} failed in {DurationMs} ms",
            command.Count,
            command.Parallelism,
            result.Published,
            result.Failed,
            result.Duration.TotalMilliseconds
        );
        return result;
    }
}

public sealed class GetQueueStatusHandler(PublisherChannelPool channels)
    : IQueryHandler<GetQueueStatus, QueueStatus>
{
    public async ValueTask<QueueStatus> Handle(GetQueueStatus query, CancellationToken ct)
    {
        // Passive declare = "tell me about this queue"; borrows a pooled channel like any other caller.
        await using var lease = await channels.RentAsync(ct);
        var queue = await lease.Channel.QueueDeclarePassiveAsync(MessagingTopology.Queue, ct);
        var dead = await lease.Channel.QueueDeclarePassiveAsync(
            MessagingTopology.DeadLetterQueue,
            ct
        );
        return new QueueStatus(queue.MessageCount, queue.ConsumerCount, dead.MessageCount);
    }
}

public sealed class GetReceivedMessagesHandler(ReceivedMessages received)
    : IQueryHandler<GetReceivedMessages, IReadOnlyList<ReceivedMessage>>
{
    public ValueTask<IReadOnlyList<ReceivedMessage>> Handle(
        GetReceivedMessages query,
        CancellationToken ct
    ) => ValueTask.FromResult(received.Latest(query.Limit));
}

public sealed class PublishEmailHandler(MessagePublisher publisher)
    : ICommandHandler<PublishEmail, PublishedEmail>
{
    public async ValueTask<PublishedEmail> Handle(PublishEmail command, CancellationToken ct)
    {
        var routingKey = MessagingTopology.EmailRoutingKey(command.Department, command.Priority);
        var message = await publisher.PublishAsync(
            MessagingTopology.EmailsExchange,
            routingKey,
            command.Subject,
            command.ProcessingMs,
            command.Fail,
            ct
        );
        return new PublishedEmail(message, routingKey);
    }
}

public sealed class GetEmailQueuesHandler(PublisherChannelPool channels)
    : IQueryHandler<GetEmailQueues, IReadOnlyList<EmailQueueStatus>>
{
    public async ValueTask<IReadOnlyList<EmailQueueStatus>> Handle(
        GetEmailQueues query,
        CancellationToken ct
    )
    {
        await using var lease = await channels.RentAsync(ct);
        var statuses = new List<EmailQueueStatus>(MessagingTopology.EmailQueues.Count);
        foreach (var (queue, binding) in MessagingTopology.EmailQueues)
        {
            var ok = await lease.Channel.QueueDeclarePassiveAsync(queue, ct);
            statuses.Add(new EmailQueueStatus(queue, binding, ok.MessageCount, ok.ConsumerCount));
        }
        return statuses;
    }
}
