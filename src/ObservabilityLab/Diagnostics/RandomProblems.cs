using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using ObservabilityLab.Api;
using ObservabilityLab.Telemetry;

namespace ObservabilityLab.Diagnostics;

/// <summary>
/// Random problems settings. Bound from flat environment variables (RANDOM_ERROR_RATE, ...);
/// rates are probabilities 0..1 applied per /orders request.
/// </summary>
public sealed record RandomProblemsOptions
{
    [ConfigurationKeyName("RANDOM_PROBLEMS_ENABLED")]
    public bool Enabled { get; init; }

    [ConfigurationKeyName("RANDOM_ERROR_RATE"), Range(0d, 1d)]
    public double ErrorRate { get; init; }

    [ConfigurationKeyName("RANDOM_EXCEPTION_RATE"), Range(0d, 1d)]
    public double ExceptionRate { get; init; }

    [ConfigurationKeyName("RANDOM_SLOW_REQUEST_RATE"), Range(0d, 1d)]
    public double SlowRequestRate { get; init; }

    [ConfigurationKeyName("RANDOM_DB_ERROR_RATE"), Range(0d, 1d)]
    public double DbErrorRate { get; init; }

    [ConfigurationKeyName("RANDOM_SLOW_DB_RATE"), Range(0d, 1d)]
    public double SlowDbRate { get; init; }

    [ConfigurationKeyName("RANDOM_VERBOSE_LOG_RATE"), Range(0d, 1d)]
    public double VerboseLogRate { get; init; }
}

/// <summary>Random problems mode: starts from configuration, can be switched at runtime via /diagnostics/random-problems.</summary>
public sealed class RandomProblems(
    IOptions<RandomProblemsOptions> options,
    ILogger<RandomProblems> logger
)
{
    private volatile RandomProblemsOptions _settings = options.Value;

    public RandomProblemsOptions Current => _settings;

    public void Update(RandomProblemsOptions settings)
    {
        _settings = settings;
        logger.LogWarning(
            "Random problems updated: enabled {Enabled}, error {ErrorRate}, exception {ExceptionRate}, slow {SlowRequestRate}, db error {DbErrorRate}, slow db {SlowDbRate}, verbose logs {VerboseLogRate}",
            settings.Enabled,
            settings.ErrorRate,
            settings.ExceptionRate,
            settings.SlowRequestRate,
            settings.DbErrorRate,
            settings.SlowDbRate,
            settings.VerboseLogRate
        );
    }

    private bool Roll(double rate) =>
        _settings.Enabled && rate > 0 && Random.Shared.NextDouble() < rate;

    /// <summary>HTTP-level problems, applied by middleware to /orders requests.</summary>
    public async Task<IResult?> MaybeHttpProblemAsync(CancellationToken ct)
    {
        if (Roll(_settings.SlowRequestRate))
        {
            var delay = Random.Shared.Next(500, 3000);
            Mark("slow_request", $"delay {delay} ms");
            logger.LogWarning("Random problem: slow request, sleeping {DelayMs} ms", delay);
            await Task.Delay(delay, ct);
        }

        if (Roll(_settings.VerboseLogRate))
        {
            Mark("verbose_logs", "burst of logs");
            for (var i = 0; i < 20; i++)
                logger.LogInformation(
                    "Random problem: noisy log line {LogLineNumber} of {LogLineTotal}",
                    i + 1,
                    20
                );
        }

        if (Roll(_settings.ExceptionRate))
        {
            Mark("exception", "unhandled exception");
            throw new InvalidOperationException("Random problem: unhandled exception injected");
        }

        if (Roll(_settings.ErrorRate))
        {
            Mark("http_500", "returned 500");
            logger.LogError("Random problem: returning HTTP 500");
            return Results.Problem(
                "Random problem: injected HTTP 500",
                statusCode: StatusCodes.Status500InternalServerError
            );
        }

        return null;
    }

    /// <summary>Database-level problems, called by handlers right before their real query.</summary>
    public async Task MaybeDatabaseProblemAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        if (Roll(_settings.SlowDbRate))
        {
            Mark("slow_db", "pg_sleep");
            await DatabaseProblems.SlowQueryAsync(conn, Random.Shared.Next(500, 2500) / 1000d, ct);
        }

        if (Roll(_settings.DbErrorRate))
        {
            Mark("db_error", "invalid SQL");
            await DatabaseProblems.FailingQueryAsync(conn, ct);
        }
    }

    private static void Mark(string kind, string description)
    {
        LabTelemetry.RecordProblem(kind, "random");
        Activity.Current?.AddEvent(
            new ActivityEvent(
                "random_problem",
                tags: new ActivityTagsCollection
                {
                    ["problem.kind"] = kind,
                    ["problem.description"] = description,
                }
            )
        );
    }
}

public static class DatabaseProblems
{
    public static Task SlowQueryAsync(
        NpgsqlConnection conn,
        double seconds,
        CancellationToken ct
    ) =>
        conn.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_sleep(@seconds)",
                new { seconds },
                cancellationToken: ct
            )
        );

    public static Task FailingQueryAsync(NpgsqlConnection conn, CancellationToken ct) =>
        conn.ExecuteAsync(
            new CommandDefinition(
                "SELECT * FROM orders_table_that_does_not_exist",
                cancellationToken: ct
            )
        );
}

public sealed class RandomProblemsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RandomProblems problems)
    {
        if (
            context.Request.Path.StartsWithSegments(ApiRoutes.Orders.Base)
            && await problems.MaybeHttpProblemAsync(context.RequestAborted) is { } result
        )
        {
            await result.ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
