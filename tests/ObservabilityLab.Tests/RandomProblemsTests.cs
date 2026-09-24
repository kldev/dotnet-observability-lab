using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ObservabilityLab.Diagnostics;

namespace ObservabilityLab.Tests;

public sealed class RandomProblemsTests
{
    private static RandomProblems Create(RandomProblemsOptions options) =>
        new(Options.Create(options), NullLogger<RandomProblems>.Instance);

    [Fact]
    public async Task Disabled_mode_never_injects_problems_even_with_rate_1()
    {
        var problems = Create(
            new()
            {
                Enabled = false,
                ErrorRate = 1,
                ExceptionRate = 1,
            }
        );

        for (var i = 0; i < 100; i++)
            Assert.Null(await problems.MaybeHttpProblemAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Enabled_mode_with_error_rate_1_always_returns_500()
    {
        var problems = Create(new() { Enabled = true, ErrorRate = 1 });

        var result = await problems.MaybeHttpProblemAsync(CancellationToken.None);

        var status = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(
            result
        );
        Assert.Equal(500, status.StatusCode);
    }

    [Fact]
    public async Task Enabled_mode_with_exception_rate_1_throws()
    {
        var problems = Create(new() { Enabled = true, ExceptionRate = 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            problems.MaybeHttpProblemAsync(CancellationToken.None)
        );
    }

    [Fact]
    public void Update_switches_settings_at_runtime()
    {
        var problems = Create(new());

        problems.Update(new() { Enabled = true, SlowRequestRate = 0.5 });

        Assert.True(problems.Current.Enabled);
        Assert.Equal(0.5, problems.Current.SlowRequestRate);
    }
}
