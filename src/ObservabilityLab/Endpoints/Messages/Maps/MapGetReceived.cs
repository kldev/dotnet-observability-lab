using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using ObservabilityLab.Api;
using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Messages.Maps;

internal static class MapGetReceived
{
    // GET /api/messages/received?limit= - consumer: what it processed lately
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(ApiRoutes.Messages.Received, Handler)
            .WithName("Get received messages")
            .WithSummary("Get messages the consumer processed")
            .WithDescription(
                $"Newest first, from memory: only the last {ReceivedMessages.Capacity} messages since the app started. Covers every consumed queue (lab.messages and q.emails.*) - an email bound to several queues shows up once per queue. ThreadId shows handlers running in parallel."
            )
            .Produces<IReadOnlyList<ReceivedMessageResponse>>()
            .ProducesStandardErrors();

    private static async Task<Ok<IReadOnlyList<ReceivedMessageResponse>>> Handler(
        [Description("Messages to return, 1-200 (default 50).")]
        [Range(1, ReceivedMessages.Capacity)]
            int? limit,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var received = await mediator.Send(new GetReceivedMessages(limit ?? 50), ct);
        return TypedResults.Ok<IReadOnlyList<ReceivedMessageResponse>>([
            .. received.Select(ReceivedMessageResponse.From),
        ]);
    }
}
