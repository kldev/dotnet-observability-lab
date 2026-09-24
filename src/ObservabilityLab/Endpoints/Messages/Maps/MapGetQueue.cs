using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ObservabilityLab.Api;
using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Messages.Maps;

internal static class MapGetQueue
{
    // GET /api/messages/queue - broker view of the queue and its dead-letter queue
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(ApiRoutes.Messages.Queue, Handler)
            .WithName("Get queue status")
            .WithSummary("Get the queue status")
            .WithDescription(
                "As reported by the broker: messages ready for delivery (not counting the ones being processed), consumers, and messages in the dead-letter queue."
            )
            .Produces<QueueStatusResponse>()
            .ProducesStandardErrors()
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<Ok<QueueStatusResponse>> Handler(
        IMediator mediator,
        CancellationToken ct
    ) => TypedResults.Ok(QueueStatusResponse.From(await mediator.Send(new GetQueueStatus(), ct)));
}
