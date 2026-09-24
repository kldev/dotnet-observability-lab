using Npgsql;
using ObservabilityLab.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ObservabilityLab.Hosting;

public static class TelemetryExtensions
{
    /// <summary>JSON logs to stdout (with TraceId/SpanId scopes) and OpenTelemetry: traces + logs via OTLP, metrics via /metrics.</summary>
    /// <remarks>OTLP endpoint comes from the standard OTEL_EXPORTER_OTLP_ENDPOINT variable.</remarks>
    public static IHostApplicationBuilder AddLabTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.Configure(o =>
            o.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId
                | ActivityTrackingOptions.SpanId
                | ActivityTrackingOptions.ParentId
        );
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        builder
            .Services.AddOpenTelemetry()
            .ConfigureResource(r =>
                r.AddService(
                        LabTelemetry.ServiceName,
                        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()
                    )
                    .AddAttributes([
                        new("deployment.environment.name", builder.Environment.EnvironmentName),
                    ])
            )
            .WithTracing(t =>
                t.AddSource(LabTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation(o =>
                    {
                        // Scrapes and docker health probes would drown real traffic; health is visible as the lab.health.status metric.
                        o.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/metrics")
                            && !ctx.Request.Path.StartsWithSegments("/health");
                        o.RecordException = true;
                    })
                    .AddNpgsql()
                    .AddOtlpExporter()
            )
            .WithMetrics(m =>
                m.AddMeter(
                        LabTelemetry.SourceName,
                        "Microsoft.AspNetCore.Hosting",
                        "Microsoft.AspNetCore.Server.Kestrel",
                        "Microsoft.AspNetCore.Diagnostics",
                        "System.Runtime",
                        "Npgsql"
                    )
                    .SetExemplarFilter(ExemplarFilterType.TraceBased)
                    .AddPrometheusExporter()
            )
            .WithLogging(
                l => l.AddOtlpExporter(),
                o =>
                {
                    o.IncludeScopes = true;
                    o.IncludeFormattedMessage = true;
                }
            );

        return builder;
    }
}
