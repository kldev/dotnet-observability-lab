using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ObservabilityLab.Telemetry;

/// <summary>Publishes periodic health check results as the lab.health.status gauge (1 = healthy, 0.5 = degraded, 0 = unhealthy).</summary>
public sealed class HealthMetricsPublisher : IHealthCheckPublisher
{
    private readonly ConcurrentDictionary<string, double> _status = new();

    public HealthMetricsPublisher()
    {
        LabTelemetry.Meter.CreateObservableGauge(
            "lab.health.status",
            () =>
                _status.Select(s => new System.Diagnostics.Metrics.Measurement<double>(
                    s.Value,
                    new KeyValuePair<string, object?>("check", s.Key)
                )),
            description: "Health check status (1 healthy, 0.5 degraded, 0 unhealthy)"
        );
    }

    public Task PublishAsync(HealthReport report, CancellationToken ct)
    {
        _status["overall"] = Score(report.Status);
        foreach (var (name, entry) in report.Entries)
            _status[name] = Score(entry.Status);
        return Task.CompletedTask;
    }

    private static double Score(HealthStatus status) =>
        status switch
        {
            HealthStatus.Healthy => 1,
            HealthStatus.Degraded => 0.5,
            _ => 0,
        };
}
