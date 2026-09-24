using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ObservabilityLab.Api;
using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Messages.Maps;

internal static class MapPublish
{
    // Declared public so the .NET 10 validation generator picks it up; still internal in effect (containing class is internal).
    public sealed record PublishMessageRequest(
        [property: Description(
            "Message text, 1-1000 characters. Surrounding whitespace is trimmed."
        )]
        [property: Required, StringLength(1000)]
            string Text,
        [property: Description(
            "How long the consumer works on the message, 0-60000 ms (default 0)."
        )]
        [property: Range(0, 60_000)]
            int ProcessingMs = 0,
        [property: Description(
            "When true the consumer fails on the message and it goes to the dead-letter queue."
        )]
            bool Fail = false
    )
    {
        public PublishMessage ToCommand() => new(Text.Trim(), ProcessingMs, Fail);
    }

    // POST /api/messages - producer: publish one message
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapPost(ApiRoutes.Messages.Publish, Handler)
            .WithName("Publish message")
            .WithSummary("Publish a message to the queue")
            .WithDescription(
                "Returns once the broker confirms the message; processing happens later in the background consumer (see Get received messages)."
            )
            .Produces<PublishedMessageResponse>(StatusCodes.Status202Accepted)
            .ProducesStandardErrors()
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<Accepted<PublishedMessageResponse>> Handler(
        PublishMessageRequest request,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var message = await mediator.Send(request.ToCommand(), ct);
        return TypedResults.Accepted((string?)null, PublishedMessageResponse.From(message));
    }
}
