using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using ObservabilityLab.Api;
using ObservabilityLab.Orders;

namespace ObservabilityLab.Endpoints.Orders.Maps;

internal static class MapGetSlice
{
    // GET /api/orders?status=&page=&pageSize= - newest orders first
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints
            .MapGet(ApiRoutes.Orders.Slice, Handler)
            .WithName("Get orders")
            .WithSummary("Get a slice of orders")
            .WithDescription("Newest first. All statuses when status is left out.")
            .Produces<SliceResponse<OrderResponse>>()
            .ProducesStandardErrors();

    private static async Task<Ok<SliceResponse<OrderResponse>>> Handler(
        [Description("Created, Paid, Cancelled or Completed.")]
        [EnumDataType(typeof(OrderStatus))]
            OrderStatus? status,
        [Description("1-based page number (default 1).")] [Range(1, 100_000)] int? page,
        [Description("Orders per page, 1-500 (default 50).")] [Range(1, 500)] int? pageSize,
        IMediator mediator,
        CancellationToken ct
    )
    {
        var query = new GetOrders(status, page ?? 1, pageSize ?? 50);
        var slice = await mediator.Send(query, ct);
        return TypedResults.Ok(
            new SliceResponse<OrderResponse>(
                [.. slice.Items.Select(OrderResponse.From)],
                query.Page,
                query.PageSize,
                slice.HasMore
            )
        );
    }
}
