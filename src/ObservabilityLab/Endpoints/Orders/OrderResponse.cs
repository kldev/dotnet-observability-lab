using ObservabilityLab.Orders;

namespace ObservabilityLab.Endpoints.Orders;

/// <summary>Order as returned by the API (the contract, independent of the Dapper read model).</summary>
public sealed record OrderResponse(
    Guid Id,
    string CustomerName,
    decimal TotalAmount,
    DateTime CreatedAt,
    OrderStatus Status
)
{
    internal static OrderResponse From(Order order) =>
        new(order.Id, order.CustomerName, order.TotalAmount, order.CreatedAt, order.Status);
}
