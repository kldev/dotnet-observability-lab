using System.Diagnostics;
using Mediator;

namespace ObservabilityLab.Telemetry;

/// <summary>Wraps every Mediator message in a span so traces show HTTP → Mediator → handler → PostgreSQL.</summary>
public sealed class MediatorTracingBehavior<TMessage, TResponse>(
    ILogger<MediatorTracingBehavior<TMessage, TResponse>> logger
) : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
{
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken ct
    )
    {
        var name = typeof(TMessage).Name;
        using var activity = LabTelemetry.Source.StartActivity($"Mediator {name}");
        activity?.SetTag("mediator.message", name);
        activity?.SetTag("mediator.kind", message is ICommand<TResponse> ? "command" : "query");

        var start = Stopwatch.GetTimestamp();
        try
        {
            return await next(message, ct);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(
                ex,
                "Mediator message {MediatorMessage} failed after {ElapsedMs} ms",
                name,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds
            );
            throw;
        }
    }
}
