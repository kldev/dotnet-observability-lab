using System.Text.Json.Serialization;
using Dapper;
using Mediator;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using ObservabilityLab;
using ObservabilityLab.Api;
using ObservabilityLab.Diagnostics;
using ObservabilityLab.Endpoints;
using ObservabilityLab.Telemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// --- Data access: one NpgsqlDataSource + Dapper, nothing more -------------------------------
DefaultTypeMap.MatchNamesWithUnderscores = true;
SqlMapper.AddTypeHandler(new OrderStatusHandler());
builder.Services.AddSingleton(_ =>
{
    var dataSource = new NpgsqlDataSourceBuilder(
        builder.Configuration.GetConnectionString("Orders")
            ?? throw new InvalidOperationException("ConnectionStrings:Orders is missing")
    );
    dataSource.Name = "orders"; // pool name used in Npgsql metric labels
    // Span name = the SQL itself (shortened), e.g. "SELECT pg_sleep(@seconds)" instead of just "postgresql".
    dataSource.ConfigureTracing(o =>
        o.ConfigureCommandSpanNameProvider(cmd => SqlSpanName(cmd.CommandText))
    );
    return dataSource.Build();
});

// --- Mediator (source generated) with a tracing pipeline behavior -----------------------------
builder.Services.AddMediator(
    (MediatorOptions o) =>
    {
        o.ServiceLifetime = ServiceLifetime.Scoped;
        o.PipelineBehaviors = [typeof(MediatorTracingBehavior<,>)];
    }
);

builder.Services.AddSingleton(TimeProvider.System);
builder
    .Services.AddOptions<RandomProblemsOptions>()
    .Bind(builder.Configuration)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<RandomProblems>();

// --- HTTP API: ProblemDetails (with traceId), built-in validation, OpenAPI + Scalar -----------
builder.Services.AddProblemDetails();

// Bad input that fails binding (e.g. unknown enum value) is a 400, not a 500 - also in Development,
// where minimal APIs throw BadHttpRequestException instead of answering 400 directly.
builder.Services.Configure<ExceptionHandlerOptions>(o =>
    o.StatusCodeSelector = ex =>
        ex is BadHttpRequestException bad
            ? bad.StatusCode
            : StatusCodes.Status500InternalServerError
);
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false))
);
builder.Services.AddOpenApi(
    "v1",
    o =>
    {
        o.AddDocumentTransformer(
            (document, _, _) =>
            {
                document.Info.Title = "Observability Lab";
                document.Info.Version = "v1";
                document.Info.Description = """
                    A deliberately small Orders API that exists to generate logs, metrics and traces
                    for the observability stack (Prometheus, Grafana, Rootprint on RustFS/S3).
                    Problem-injection endpoints (/diagnostics/*) are lab tools and are not part of this document.
                    """;
                return Task.CompletedTask;
            }
        );
        o.AddTagDescriptions();
    }
);

// --- Health checks (also published as the lab.health.status metric) ---------------------------
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

// --- Logging: JSON to stdout (with TraceId/SpanId/RequestId scopes) + OTLP -------------------
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

// --- OpenTelemetry: traces + logs via OTLP (collector -> Rootprint), metrics via /metrics ------
// OTLP endpoint comes from the standard OTEL_EXPORTER_OTLP_ENDPOINT variable.
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

var app = builder.Build();

await Database.EnsureSchemaAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<RandomProblemsMiddleware>();

app.MapPrometheusScrapingEndpoint("/metrics");
app.MapHealthChecks(
    "/health",
    new HealthCheckOptions
    {
        Predicate = c => c.Tags.Contains("live"),
        ResponseWriter = HealthJson.Write,
    }
);
app.MapHealthChecks("/health/ready", new HealthCheckOptions { ResponseWriter = HealthJson.Write });

app.MapOpenApi(); // /openapi/v1.json
app.MapScalarApiReference("/docs", o => o.Title = "Observability Lab API");

app.MapGet("/", () => TypedResults.Redirect("/docs")).ExcludeFromDescription();
app.MapLabEndpoints();

app.Run();

static string SqlSpanName(string sql)
{
    var oneLine = string.Join(
        ' ',
        sql.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
    );
    return oneLine.Length <= 60 ? oneLine : oneLine[..60] + "…";
}

public partial class Program;
