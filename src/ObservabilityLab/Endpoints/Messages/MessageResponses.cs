using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Messages;

/// <summary>A message accepted by the broker (confirmed), not yet processed.</summary>
public sealed record PublishedMessageResponse(Guid Id, DateTimeOffset PublishedAt)
{
    internal static PublishedMessageResponse From(LabMessage message) =>
        new(message.Id, message.PublishedAt);
}

public sealed record PublishBurstResponse(
    int Published,
    int Failed,
    int Parallelism,
    double DurationMs,
    double MessagesPerSecond
)
{
    internal static PublishBurstResponse From(BurstResult result, int parallelism) =>
        new(
            result.Published,
            result.Failed,
            parallelism,
            Math.Round(result.Duration.TotalMilliseconds, 1),
            Math.Round(result.Published / Math.Max(result.Duration.TotalSeconds, 0.001), 1)
        );
}

public sealed record ReceivedMessageResponse(
    Guid Id,
    string Queue,
    string RoutingKey,
    string Text,
    MessageOutcome Outcome,
    DateTimeOffset PublishedAt,
    DateTimeOffset CompletedAt,
    double EndToEndMs,
    bool Redelivered,
    int ThreadId
)
{
    internal static ReceivedMessageResponse From(ReceivedMessage received) =>
        new(
            received.Message.Id,
            received.Queue,
            received.RoutingKey,
            received.Message.Text,
            received.Outcome,
            received.Message.PublishedAt,
            received.CompletedAt,
            Math.Round((received.CompletedAt - received.Message.PublishedAt).TotalMilliseconds, 1),
            received.Redelivered,
            received.ThreadId
        );
}

public sealed record QueueStatusResponse(
    string Queue,
    uint Ready,
    uint Consumers,
    string DeadLetterQueue,
    uint DeadLettered
)
{
    internal static QueueStatusResponse From(QueueStatus status) =>
        new(
            MessagingTopology.Queue,
            status.Ready,
            status.Consumers,
            MessagingTopology.DeadLetterQueue,
            status.DeadLettered
        );
}
