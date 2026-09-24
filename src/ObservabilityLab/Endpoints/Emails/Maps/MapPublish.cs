using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ObservabilityLab.Api;
using ObservabilityLab.Messaging;

namespace ObservabilityLab.Endpoints.Emails.Maps;

internal static class MapPublish
{
    // Declared public so the .NET 10 validation generator picks it up; still internal in effect (containing class is internal).
    public sealed record PublishEmailRequest(
        [property: Description(
            "Recruitment, Sales or Support - the first word of the routing key."
        )]
        // JsonRequired: a missing enum would silently become its default (Recruitment) instead of a 400.
        [property: JsonRequired, EnumDataType(typeof(EmailDepartment))]
            EmailDepartment Department,
        [property: Description(
            "Email subject, 1-300 characters. Surrounding whitespace is trimmed."
        )]
        [property: Required, StringLength(300)]
            string Subject,
        [property: Description(
            "Normal or Urgent (default Normal) - the second word of the routing key; urgent emails also reach q.emails.urgent."
        )]
        [property: EnumDataType(typeof(EmailPriority))]
            EmailPriority Priority = EmailPriority.Normal,
        [property: Description("How long the consumer works on the email, 0-60000 ms (default 0).")]
        [property: Range(0, 60_000)]
            int ProcessingMs = 0,
        [property: Description(
            "When true the consumer fails on the email in every queue and it goes to the dead-letter queue."
        )]
            bool Fail = false
    )
    {
        public PublishEmail ToCommand() =>
            new(Department, Priority, Subject.Trim(), ProcessingMs, Fail);
    }

    // POST /api/emails - producer: publish to the x.emails topic exchange
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapPost(ApiRoutes.Emails.Publish, Handler)
            .WithName("Publish email")
            .WithSummary("Publish an email to the x.emails topic exchange")
            .WithDescription(
                "Routing key <department>.<priority>, e.g. sales.urgent. The response lists the q.emails.* queues whose binding matches it; the consumer processes the email once per queue."
            )
            .Produces<PublishedEmailResponse>(StatusCodes.Status202Accepted)
            .ProducesStandardErrors()
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

    private static async Task<Accepted<PublishedEmailResponse>> Handler(
        PublishEmailRequest request,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var email = await mediator.Send(request.ToCommand(), ct);
        return TypedResults.Accepted((string?)null, PublishedEmailResponse.From(email));
    }
}
