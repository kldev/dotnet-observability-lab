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
