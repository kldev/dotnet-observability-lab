using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using ObservabilityLab.Api;
using ObservabilityLab.Orders;

namespace ObservabilityLab.Endpoints.Orders.Maps;

internal static class MapGet
{
    // GET /api/orders/{orderId} - one order
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(ApiRoutes.Orders.Get, Handler)
            .WithName("Get order")
            .WithSummary("Get an order")
            .Produces<OrderResponse>()
            .ProducesStandardErrors();

    private static async Task<Results<Ok<OrderResponse>, NotFound>> Handler(
        Guid orderId,
        IMediator mediator,
        CancellationToken ct
    ) =>
        await mediator.Send(new GetOrder(orderId), ct) is { } order
            ? TypedResults.Ok(OrderResponse.From(order))
            : TypedResults.NotFound();
}
