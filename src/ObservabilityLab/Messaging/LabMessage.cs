namespace ObservabilityLab.Messaging;

/// <summary>
/// What travels on the queue (JSON). <see cref="ProcessingMs"/> and <see cref="Fail"/> tell the consumer
/// how to behave, so slow and failing consumers can be produced on demand.
/// </summary>
public sealed record LabMessage(
    Guid Id,
    string Text,
    int ProcessingMs,
    bool Fail,
    DateTimeOffset PublishedAt
);

public enum MessageOutcome
{
    Acked,
    DeadLettered,
}

/// <summary>A message the consumer finished with. <see cref="ThreadId"/> shows handlers running on many threads.</summary>
public sealed record ReceivedMessage(
    LabMessage Message,
    MessageOutcome Outcome,
    DateTimeOffset CompletedAt,
    bool Redelivered,
    int ThreadId
);

/// <summary>The last messages the consumer finished with - shows the consumer side without a database.</summary>
public sealed class ReceivedMessages
{
    public const int Capacity = 200;

    private readonly Lock _lock = new();
    private readonly Queue<ReceivedMessage> _items = new(Capacity);

    // Called concurrently by consumer handlers (ConsumerConcurrency > 1).
    public void Add(ReceivedMessage message)
    {
        lock (_lock)
        {
            if (_items.Count == Capacity)
                _items.Dequeue();
            _items.Enqueue(message);
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ReceivedMessage> Latest(int limit)
    {
        lock (_lock)
            return [.. _items.Reverse().Take(limit)];
    }
}
