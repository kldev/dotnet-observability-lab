using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using ObservabilityLab.Api;
using ObservabilityLab.Orders;

namespace ObservabilityLab.Endpoints.Orders.Maps;

internal static class MapChangeStatus
{
    // Declared public so the .NET 10 validation generator picks it up; still internal in effect (containing class is internal).
    public sealed record ChangeOrderStatusRequest(
        [property: Description(
            "Created, Paid, Cancelled or Completed. Any transition is allowed - this is a lab."
        )]
        [property: EnumDataType(typeof(OrderStatus))]
            OrderStatus Status
    )
    {
        public ChangeOrderStatus ToCommand(Guid orderId) => new(orderId, Status);
    }

    // PUT /api/orders/{orderId}/status - change the status
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapPut(ApiRoutes.Orders.ChangeStatus, Handler)
            .WithName("Change order status")
            .WithSummary("Change an order's status")
            .Produces<OrderResponse>()
            .ProducesStandardErrors();

    private static async Task<Results<Ok<OrderResponse>, NotFound>> Handler(
        Guid orderId,
        ChangeOrderStatusRequest request,
        IMediator mediator,
        CancellationToken ct
    ) =>
        await mediator.Send(request.ToCommand(orderId), ct) is { } order
            ? TypedResults.Ok(OrderResponse.From(order))
            : TypedResults.NotFound();
}
