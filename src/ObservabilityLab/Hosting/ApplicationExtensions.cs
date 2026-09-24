using Mediator;
using ObservabilityLab.Diagnostics;
using ObservabilityLab.Telemetry;

namespace ObservabilityLab.Hosting;

public static class ApplicationExtensions
{
    /// <summary>Mediator (source generated) with a tracing pipeline behavior, time and random problems mode.</summary>
    public static IHostApplicationBuilder AddApplication(this IHostApplicationBuilder builder)
    {
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

        return builder;
    }
}
