namespace ObservabilityLab.Orders;

public enum OrderStatus
{
    Created,
    Paid,
    Cancelled,
    Completed,
}

public sealed record Order(
    Guid Id,
    string CustomerName,
    decimal TotalAmount,
    DateTime CreatedAt,
    OrderStatus Status
);
