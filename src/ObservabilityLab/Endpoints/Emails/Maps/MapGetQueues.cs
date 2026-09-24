using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ObservabilityLab.Api;
using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Emails.Maps;

internal static class MapGetQueues
{
    // GET /api/emails/queues - the q.emails.* queues, their bindings and broker counts
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(ApiRoutes.Emails.Queues, Handler)
            .WithName("Get email queues")
            .WithSummary("Get the email queues and their bindings")
            .WithDescription(
                "Binding patterns on x.emails: * matches one word, # zero or more. Ready counts come from the broker."
            )
            .Produces<IReadOnlyList<EmailQueueResponse>>()
            .ProducesStandardErrors()
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<Ok<IReadOnlyList<EmailQueueResponse>>> Handler(
        IMediator mediator,
        CancellationToken ct
    )
    {
        var queues = await mediator.Send(new GetEmailQueues(), ct);
        return TypedResults.Ok<IReadOnlyList<EmailQueueResponse>>([
            .. queues.Select(EmailQueueResponse.From),
        ]);
    }
}
