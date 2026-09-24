using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using ObservabilityLab.Api;
using ObservabilityLab.Orders;

namespace ObservabilityLab.Endpoints.Orders.Maps;

internal static class MapCreate
{
    // Declared public so the .NET 10 validation generator picks it up; still internal in effect (containing class is internal).
    public sealed record CreateOrderRequest(
        [property: Description(
            "Customer name, 1-200 characters. Surrounding whitespace is trimmed."
        )]
        [property: Required, StringLength(200)]
            string CustomerName,
        [property: Description("Order total, greater than 0.")]
        [property: Range(typeof(decimal), "0.01", "1000000000")]
            decimal TotalAmount
    )
    {
        public CreateOrder ToCommand() => new(CustomerName.Trim(), TotalAmount);
    }

    // POST /api/orders - create an order
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapPost(ApiRoutes.Orders.Create, Handler)
            .WithName("Create order")
            .WithSummary("Create an order")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .ProducesStandardErrors();

    private static async Task<Created<OrderResponse>> Handler(
        CreateOrderRequest request,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var order = await mediator.Send(request.ToCommand(), ct);
        return TypedResults.Created(ApiRoutes.Orders.For(order.Id), OrderResponse.From(order));
    }
}
