using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using ObservabilityLab.Api;

namespace ObservabilityLab.Orders;

/// <summary>Orders API. Endpoints only translate HTTP to Mediator messages; input is validated by the built-in .NET 10 validation.</summary>
public static class OrderEndpoints
{
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

    public sealed record ChangeOrderStatusRequest(
        [property: Description(
            "Created, Paid, Cancelled or Completed. Any transition is allowed - this is a lab."
        )]
        [property: EnumDataType(typeof(OrderStatus))]
            OrderStatus Status
    );

    public sealed record OrderResponse(
        Guid Id,
        string CustomerName,
        decimal TotalAmount,
        DateTime CreatedAt,
        OrderStatus Status
    )
    {
        public static OrderResponse From(Order o) =>
            new(o.Id, o.CustomerName, o.TotalAmount, o.CreatedAt, o.Status);
    }

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("").WithTags(ApiTags.Orders);

        // POST /api/orders - create an order
        orders
            .MapPost(ApiRoutes.Orders.Create, CreateOrder)
            .WithName("Create order")
            .WithSummary("Create an order")
            .ProducesStandardErrors();

        // GET /api/orders/{orderId} - one order
        orders
            .MapGet(ApiRoutes.Orders.Get, GetOrder)
            .WithName("Get order")
            .WithSummary("Get an order")
            .ProducesStandardErrors();

        // GET /api/orders?status=&limit= - newest orders first
        orders
            .MapGet(ApiRoutes.Orders.List, GetOrders)
            .WithName("Get orders")
            .WithSummary("Get the newest orders")
            .WithDescription(
                "Newest first. Optionally filtered by status; limit defaults to 50 and is capped at 500."
            )
            .ProducesStandardErrors();

        // PUT /api/orders/{orderId}/status - change the status
        orders
            .MapPut(ApiRoutes.Orders.ChangeStatus, ChangeOrderStatus)
            .WithName("Change order status")
            .WithSummary("Change an order's status")
            .ProducesStandardErrors();
    }

    private static async Task<Created<OrderResponse>> CreateOrder(
        CreateOrderRequest request,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var order = await mediator.Send(request.ToCommand(), ct);
        return TypedResults.Created(ApiRoutes.Orders.For(order.Id), OrderResponse.From(order));
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound>> GetOrder(
        Guid orderId,
        IMediator mediator,
        CancellationToken ct
    ) =>
        await mediator.Send(new GetOrder(orderId), ct) is { } order
            ? TypedResults.Ok(OrderResponse.From(order))
            : TypedResults.NotFound();

    private static async Task<Ok<IEnumerable<OrderResponse>>> GetOrders(
        [Description("Created, Paid, Cancelled or Completed. All statuses when left out.")]
        [EnumDataType(typeof(OrderStatus))]
            OrderStatus? status,
        [Description("Maximum number of orders, 1-500 (default 50).")] [Range(1, 500)] int? limit,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var orders = await mediator.Send(new GetOrders(status, limit ?? 50), ct);
        return TypedResults.Ok(orders.Select(OrderResponse.From));
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound>> ChangeOrderStatus(
        Guid orderId,
        ChangeOrderStatusRequest request,
        IMediator mediator,
        CancellationToken ct
    ) =>
        await mediator.Send(new ChangeOrderStatus(orderId, request.Status), ct) is { } order
            ? TypedResults.Ok(OrderResponse.From(order))
            : TypedResults.NotFound();
}
