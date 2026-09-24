using System.ComponentModel.DataAnnotations;

namespace ObservabilityLab.Messaging;

/// <summary>RabbitMQ tuning, bound from the "RabbitMq" section (RabbitMq__PublisherChannels, ...). The broker address is ConnectionStrings:RabbitMq.</summary>
public sealed record RabbitMqOptions
{
    public const string Section = "RabbitMq";

    /// <summary>Most publisher channels open at once; more concurrent publishers wait for a free one.</summary>
    [Range(1, 64)]
    public int PublisherChannels { get; init; } = 4;

    /// <summary>How many messages the consumer processes in parallel (handlers run on the thread pool).</summary>
    [Range(1, 64)]
    public int ConsumerConcurrency { get; init; } = 4;

    /// <summary>Unacked messages the broker pushes to the consumer ahead of processing.</summary>
    [Range(1, 1000)]
    public int Prefetch { get; init; } = 32;
}
