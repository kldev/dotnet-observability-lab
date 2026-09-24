using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ObservabilityLab.Telemetry;

namespace ObservabilityLab.Hosting;

public static class HealthCheckExtensions
{
    /// <summary>Liveness + PostgreSQL readiness, also published as the lab.health.status metric.</summary>
    public static IHostApplicationBuilder AddLabHealthChecks(this IHostApplicationBuilder builder)
    {
        builder
            .Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        builder.Services.AddSingleton<IHealthCheckPublisher, HealthMetricsPublisher>();
        builder.Services.Configure<HealthCheckPublisherOptions>(o =>
        {
            o.Delay = TimeSpan.FromSeconds(5);
            o.Period = TimeSpan.FromSeconds(15);
        });

        return builder;
    }

    /// <summary>/health (liveness only) and /health/ready (every check).</summary>
    public static WebApplication MapLabHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks(
            "/health",
            new HealthCheckOptions
            {
                Predicate = c => c.Tags.Contains("live"),
                ResponseWriter = HealthJson.Write,
            }
        );
        app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions { ResponseWriter = HealthJson.Write }
        );
        return app;
    }
}
