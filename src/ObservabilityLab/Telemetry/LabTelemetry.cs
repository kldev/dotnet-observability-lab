using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ObservabilityLab.Telemetry;

/// <summary>Application-specific ActivitySource and Meter (everything else comes from built-in instrumentation).</summary>
public static class LabTelemetry
{
    public const string ServiceName = "observability-lab";
    public const string SourceName = "ObservabilityLab";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> OrdersCreated = Meter.CreateCounter<long>(
        "lab.orders.created",
        "{order}",
        "Orders created"
    );

    public static readonly Counter<long> OrderStatusChanges = Meter.CreateCounter<long>(
        "lab.orders.status_changes",
        "{order}",
        "Order status changes by target status"
    );

    public static readonly Histogram<double> OrderAmount = Meter.CreateHistogram<double>(
        "lab.orders.amount",
        "{PLN}",
        "Total amount of created orders"
    );

    public static readonly Counter<long> ProblemsInjected = Meter.CreateCounter<long>(
        "lab.problems.injected",
        "{problem}",
        "Problems injected on purpose (diagnostics + random mode)"
    );

    // Seconds-based buckets from 1 ms to 30 s (the SDK default buckets assume milliseconds).
    private static readonly InstrumentAdvice<double> SecondsBuckets = new()
    {
        HistogramBucketBoundaries =
        [
            0.001,
            0.0025,
            0.005,
            0.01,
            0.025,
            0.05,
            0.1,
            0.25,
            0.5,
            1,
            2.5,
            5,
            10,
            30,
        ],
    };

    // --- RabbitMQ: producer -------------------------------------------------------------------

    public static readonly Counter<long> MessagesPublished = Meter.CreateCounter<long>(
        "lab.messages.published",
        "{message}",
        "Messages published to RabbitMQ by outcome (confirmed by the broker, or failed)"
    );

    public static readonly Histogram<double> MessagePublishDuration = Meter.CreateHistogram(
        "lab.messages.publish.duration",
        "s",
        "Publish including the wait for a pooled channel and the broker confirm",
        advice: SecondsBuckets
    );

    public static readonly Histogram<double> PublisherChannelWait = Meter.CreateHistogram(
        "lab.rabbitmq.publisher.channel.wait",
        "s",
        "Time a publisher waited for a free channel (grows when the pool is too small)",
        advice: SecondsBuckets
    );

    public static readonly UpDownCounter<long> PublisherChannelsInUse =
        Meter.CreateUpDownCounter<long>(
            "lab.rabbitmq.publisher.channels.in_use",
            "{channel}",
            "Publisher channels currently lent to a caller"
        );

    public static readonly Counter<long> PublisherChannelsOpened = Meter.CreateCounter<long>(
        "lab.rabbitmq.publisher.channels.opened",
        "{channel}",
        "Publisher channels opened (flat when the pool reuses them)"
    );

    // --- RabbitMQ: consumer -------------------------------------------------------------------

    public static readonly Counter<long> MessagesConsumed = Meter.CreateCounter<long>(
        "lab.messages.consumed",
        "{message}",
        "Messages the consumer finished with, by outcome (Acked or DeadLettered)"
    );

    public static readonly Histogram<double> MessageProcessDuration = Meter.CreateHistogram(
        "lab.messages.process.duration",
        "s",
        "Time the consumer spent on one message",
        advice: SecondsBuckets
    );

    public static readonly Histogram<double> MessageEndToEndDuration = Meter.CreateHistogram(
        "lab.messages.end_to_end.duration",
        "s",
        "From publish to the end of processing (queue wait + processing)",
        advice: SecondsBuckets
    );

    public static readonly UpDownCounter<long> MessagesInFlight = Meter.CreateUpDownCounter<long>(
        "lab.messages.in_flight",
        "{message}",
        "Messages being processed by the consumer right now"
    );

    static LabTelemetry()
    {
        // Denominator for "Memory %" on the dashboard: working set / memory limit.
        Meter.CreateObservableGauge(
            "lab.process.memory.limit",
            MemoryLimitBytes,
            "By",
            "Memory the process may use (container limit, or machine memory)"
        );
    }

    private static long MemoryLimitBytes()
    {
        // cgroup v2 container limit ("max" = unlimited); otherwise what the GC sees (machine RAM).
        const string cgroupLimit = "/sys/fs/cgroup/memory.max";
        return
            File.Exists(cgroupLimit)
            && long.TryParse(File.ReadAllText(cgroupLimit).Trim(), out var limit)
            ? limit
            : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    }

    public static void RecordProblem(string kind, string source) =>
        ProblemsInjected.Add(1, new("kind", kind), new("source", source));
}
