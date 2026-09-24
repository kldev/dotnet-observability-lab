using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ObservabilityLab.Api;
using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Messages.Maps;

internal static class MapPublishBurst
{
    // Declared public so the .NET 10 validation generator picks it up; still internal in effect (containing class is internal).
    public sealed record PublishBurstRequest(
        [property: Description("Messages to publish, 1-10000.")]
        [property: Range(1, 10_000)]
            int Count,
        [property: Description(
            "Concurrent publishing tasks, 1-64 (default 16). Above the channel pool size they wait for a free channel."
        )]
        [property: Range(1, 64)]
            int Parallelism = 16,
        [property: Description("Consumer work per message, 0-10000 ms (default 0).")]
        [property: Range(0, 10_000)]
            int ProcessingMs = 0
    )
    {
        public PublishBurst ToCommand() => new(Count, Parallelism, ProcessingMs);
    }

    // POST /api/messages/burst - producer: publish many messages from many threads at once
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapPost(ApiRoutes.Messages.PublishBurst, Handler)
            .WithName("Publish message burst")
            .WithSummary("Publish many messages concurrently")
            .WithDescription(
                "Load test for the publisher channel pool: Count messages from Parallelism concurrent tasks, each on a channel of its own. Returns when all are confirmed; messages the broker rejects are counted as Failed."
            )
            .Produces<PublishBurstResponse>()
            .ProducesStandardErrors()
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<Ok<PublishBurstResponse>> Handler(
        PublishBurstRequest request,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var result = await mediator.Send(request.ToCommand(), ct);
        return TypedResults.Ok(PublishBurstResponse.From(result, request.Parallelism));
    }
}
