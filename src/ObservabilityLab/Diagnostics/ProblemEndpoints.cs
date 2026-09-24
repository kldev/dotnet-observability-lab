using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;
using ObservabilityLab.Api;
using ObservabilityLab.Telemetry;

namespace ObservabilityLab.Diagnostics;

/// <summary>
/// Endpoints that break things on purpose. Mapped only in Development or with DIAGNOSTICS_ENABLED=true.
/// They are GETs on purpose (not POST actions) so they can be triggered from a browser, curl or k6 without a body.
/// </summary>
public static class ProblemEndpoints
{
    public sealed record ProblemTriggered(string Problem, string Detail);

    // Keeps allocated memory alive until /diagnostics/problem/memory/release is called.
    private static readonly List<byte[]> HeldMemory = [];

    public static void MapProblemEndpoints(this IEndpointRouteBuilder app)
    {
        var logger = app
            .ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ObservabilityLab.Diagnostics");
        var problem = app.MapGroup(ApiRoutes.Diagnostics.Problem).ExcludeFromDescription();

        problem
            .MapGet(
                "/slow",
                async (
                    [Description("Delay in ms, 1-30000 (default 2000).")] int? ms,
                    CancellationToken ct
                ) =>
                {
                    var delay = Math.Clamp(ms ?? 2000, 1, 30_000);
                    Mark("slow_request");
                    logger.LogWarning("Diagnostics: slow request, sleeping {DelayMs} ms", delay);
                    await Task.Delay(delay, ct);
                    return TypedResults.Ok(new ProblemTriggered("slow", $"{delay} ms"));
                }
            )
            .WithName("Trigger slow request")
            .WithSummary("Respond after a delay");

        problem
            .MapGet(
                "/bad-request",
                () =>
                {
                    Mark("http_400");
                    logger.LogWarning("Diagnostics: returning HTTP 400");
                    return TypedResults.Problem("Diagnostics: injected HTTP 400", statusCode: 400);
                }
            )
            .WithName("Trigger bad request")
            .WithSummary("Respond with HTTP 400");

        problem
            .MapGet(
                "/not-found",
                () =>
                {
                    Mark("http_404");
                    logger.LogWarning("Diagnostics: returning HTTP 404");
                    return TypedResults.Problem("Diagnostics: injected HTTP 404", statusCode: 404);
                }
            )
            .WithName("Trigger not found")
            .WithSummary("Respond with HTTP 404");

        problem
            .MapGet(
                "/error",
                () =>
                {
                    Mark("http_500");
                    logger.LogError("Diagnostics: returning HTTP 500");
                    return TypedResults.Problem("Diagnostics: injected HTTP 500", statusCode: 500);
                }
            )
            .WithName("Trigger server error")
            .WithSummary("Respond with HTTP 500");

        problem
            .MapGet(
                "/exception",
                IResult () =>
                {
                    Mark("exception");
                    throw new InvalidOperationException(
                        "Diagnostics: unhandled exception injected"
                    );
                }
            )
            .WithName("Trigger exception")
            .WithSummary("Throw an unhandled exception (500 via the exception handler)");

        problem
            .MapGet(
                "/slow-db",
                async (
                    [Description("pg_sleep duration in seconds, 0.1-30 (default 2).")]
                        double? seconds,
                    NpgsqlDataSource db,
                    CancellationToken ct
                ) =>
                {
                    var s = Math.Clamp(seconds ?? 2, 0.1, 30);
                    Mark("slow_db");
                    logger.LogWarning("Diagnostics: slow PostgreSQL query, pg_sleep({Seconds})", s);
                    await using var conn = await db.OpenConnectionAsync(ct);
                    await DatabaseProblems.SlowQueryAsync(conn, s, ct);
                    return TypedResults.Ok(new ProblemTriggered("slow-db", $"pg_sleep({s})"));
                }
            )
            .WithName("Trigger slow database query")
            .WithSummary("Run a slow PostgreSQL query");

        problem
            .MapGet(
                "/db-error",
                async Task<IResult> (NpgsqlDataSource db, CancellationToken ct) =>
                {
                    Mark("db_error");
                    await using var conn = await db.OpenConnectionAsync(ct);
                    await DatabaseProblems.FailingQueryAsync(conn, ct); // throws PostgresException -> 500
                    return TypedResults.Ok();
                }
            )
            .WithName("Trigger database error")
            .WithSummary("Run a failing PostgreSQL query (missing table)");

        problem
            .MapGet(
                "/cpu",
                (
                    [Description("Duration in seconds, 1-120 (default 15).")] int? seconds,
                    [Description(
                        "Busy threads, 1-64 (default: CPU cores available to the process)."
                    )]
                        int? threads
                ) =>
                {
                    var duration = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 15, 1, 120));
                    var workers = Math.Clamp(threads ?? Environment.ProcessorCount, 1, 64);
                    Mark("high_cpu");
                    logger.LogWarning(
                        "Diagnostics: burning CPU on {Threads} threads for {Seconds} s",
                        workers,
                        duration.TotalSeconds
                    );

                    // Background threads on purpose: the request returns at once while the CPU graph goes up.
                    for (var i = 0; i < workers; i++)
                        new Thread(() => BurnCpu(duration)) { IsBackground = true }.Start();

                    return TypedResults.Accepted(
                        (string?)null,
                        new ProblemTriggered(
                            "cpu",
                            $"{workers} threads for {duration.TotalSeconds} s"
                        )
                    );
                }
            )
            .WithName("Trigger high CPU")
            .WithSummary("Keep CPU cores busy for a while");

        problem
            .MapGet(
                "/memory",
                ([Description("Megabytes to allocate and hold, 1-1024 (default 100).")] int? mb) =>
                {
                    var size = Math.Clamp(mb ?? 100, 1, 1024);
                    Mark("memory");
                    int heldMb;
                    lock (HeldMemory)
                    {
                        for (var i = 0; i < size; i++)
                        {
                            var chunk = new byte[1024 * 1024];
                            Random.Shared.NextBytes(chunk); // touch the pages so they count into the working set
                            HeldMemory.Add(chunk);
                        }
                        heldMb = HeldMemory.Count;
                    }

                    logger.LogWarning(
                        "Diagnostics: allocated {AllocatedMb} MB, holding {HeldMb} MB",
                        size,
                        heldMb
                    );
                    return TypedResults.Ok(new ProblemTriggered("memory", $"holding {heldMb} MB"));
                }
            )
            .WithName("Trigger memory allocation")
            .WithSummary("Allocate memory and keep it until released")
            .WithDescription(
                "Memory accumulates across calls; the container limit is 512 MB, so going past it gets the app OOM-killed. Use /memory/release to free it."
            );

        problem
            .MapGet(
                "/memory/release",
                () =>
                {
                    lock (HeldMemory)
                        HeldMemory.Clear();
                    GC.Collect();
                    logger.LogInformation("Diagnostics: released held memory");
                    return TypedResults.Ok(new ProblemTriggered("memory", "released"));
                }
            )
            .WithName("Release memory")
            .WithSummary("Release memory held by the memory problem");

        var random = app.MapGroup(ApiRoutes.Diagnostics.RandomProblems).ExcludeFromDescription();

        // GET /diagnostics/random-problems - current random problems settings
        random
            .MapGet("/", (RandomProblems problems) => TypedResults.Ok(problems.Current))
            .WithName("Get random problems")
            .WithSummary("Get the random problems settings");

        // PUT /diagnostics/random-problems - switch random problems on/off at runtime
        random
            .MapPut(
                "/",
                Results<Ok<RandomProblemsOptions>, ValidationProblem> (
                    RandomProblemsOptions settings,
                    RandomProblems problems
                ) =>
                {
                    double[] rates =
                    [
                        settings.ErrorRate,
                        settings.ExceptionRate,
                        settings.SlowRequestRate,
                        settings.DbErrorRate,
                        settings.SlowDbRate,
                        settings.VerboseLogRate,
                    ];
                    if (rates.Any(r => r is < 0 or > 1))
                        return TypedResults.ValidationProblem(
                            new Dictionary<string, string[]>
                            {
                                ["rates"] = ["Every rate must be between 0 and 1."],
                            }
                        );

                    problems.Update(settings);
                    return TypedResults.Ok(problems.Current);
                }
            )
            .WithName("Change random problems")
            .WithSummary("Change the random problems settings")
            .WithDescription(
                "Rates are probabilities 0-1 applied to every /api/orders request. Fields left out are 0 / false."
            );
    }

    private static void BurnCpu(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        var buffer = new byte[1024];
        while (sw.Elapsed < duration)
            SHA256.HashData(buffer, buffer.AsSpan(0, 32));
    }

    private static void Mark(string kind)
    {
        LabTelemetry.RecordProblem(kind, "diagnostics");
        Activity.Current?.SetTag("problem.kind", kind);
    }
}
