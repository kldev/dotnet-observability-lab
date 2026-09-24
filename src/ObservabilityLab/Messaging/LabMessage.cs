using System.Collections.Concurrent;

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

public enum EmailDepartment
{
    Recruitment,
    Sales,
    Support,
}

public enum EmailPriority
{
    Normal,
    Urgent,
}

public enum MessageOutcome
{
    Acked,
    DeadLettered,
}

/// <summary>A message the consumer finished with. <see cref="ThreadId"/> shows handlers running on many threads.</summary>
public sealed record ReceivedMessage(
    LabMessage Message,
    string Queue,
    string RoutingKey,
    MessageOutcome Outcome,
    DateTimeOffset CompletedAt,
    bool Redelivered,
    int ThreadId
);

/// <summary>The last messages the consumer finished with - shows the consumer side without a database.</summary>
/// <remarks>
/// Lock-free: consumer handlers of every queue add concurrently and never wait for each other or for a reader.
/// Under contention the queue can briefly hold a few more than <see cref="Capacity"/> items.
/// </remarks>
public sealed class ReceivedMessages
{
    public const int Capacity = 200;

    private readonly ConcurrentQueue<ReceivedMessage> _items = new();

    public void Add(ReceivedMessage message)
    {
        _items.Enqueue(message);
        while (_items.Count > Capacity && _items.TryDequeue(out _)) { }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ReceivedMessage> Latest(int limit)
    {
        var snapshot = _items.ToArray(); // oldest first, a consistent moment-in-time copy
        return [.. snapshot.AsEnumerable().Reverse().Take(limit)];
    }
}
