using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Emails;

/// <summary>An email accepted by the broker; <see cref="Queues"/> are the queues its routing key matches.</summary>
public sealed record PublishedEmailResponse(
    Guid Id,
    string Exchange,
    string RoutingKey,
    IReadOnlyList<string> Queues,
    DateTimeOffset PublishedAt
)
{
    internal static PublishedEmailResponse From(PublishedEmail email) =>
        new(
            email.Message.Id,
            MessagingTopology.EmailsExchange,
            email.RoutingKey,
            MessagingTopology.EmailQueuesFor(email.RoutingKey),
            email.Message.PublishedAt
        );
}

public sealed record EmailQueueResponse(string Queue, string Binding, uint Ready, uint Consumers)
{
    internal static EmailQueueResponse From(EmailQueueStatus status) =>
        new(status.Queue, status.Binding, status.Ready, status.Consumers);
}
