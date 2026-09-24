using Dapper;
using Mediator;
using Npgsql;
using ObservabilityLab.Diagnostics;
using ObservabilityLab.Telemetry;

namespace ObservabilityLab.Orders;

// Messages ------------------------------------------------------------------

public sealed record CreateOrder(string CustomerName, decimal TotalAmount) : ICommand<Order>;

public sealed record GetOrder(Guid Id) : IQuery<Order?>;

public sealed record GetOrders(OrderStatus? Status, int Limit) : IQuery<IReadOnlyList<Order>>;

public sealed record ChangeOrderStatus(Guid Id, OrderStatus Status) : ICommand<Order?>;

// Handlers ------------------------------------------------------------------
// Handlers talk to PostgreSQL directly with Dapper - no repositories on purpose.

public sealed class CreateOrderHandler(
    NpgsqlDataSource db,
    RandomProblems problems,
    TimeProvider time,
    ILogger<CreateOrderHandler> logger
) : ICommandHandler<CreateOrder, Order>
{
    public async ValueTask<Order> Handle(CreateOrder command, CancellationToken ct)
    {
        using var activity = LabTelemetry.Source.StartActivity(nameof(CreateOrderHandler));

        var order = new Order(
            Guid.CreateVersion7(),
            command.CustomerName,
            command.TotalAmount,
            time.GetUtcNow().UtcDateTime,
            OrderStatus.Created
        );
        activity?.SetTag("order.id", order.Id);

        await using var conn = await db.OpenConnectionAsync(ct);
        await problems.MaybeDatabaseProblemAsync(conn, ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO orders (id, customer_name, total_amount, created_at, status)
                VALUES (@Id, @CustomerName, @TotalAmount, @CreatedAt, @Status)
                """,
                new
                {
                    order.Id,
                    order.CustomerName,
                    order.TotalAmount,
                    order.CreatedAt,
                    Status = order.Status.ToString(),
                },
                cancellationToken: ct
            )
        );

        LabTelemetry.OrdersCreated.Add(1);
        LabTelemetry.OrderAmount.Record((double)order.TotalAmount);
        logger.LogInformation(
            "Order {OrderId} created for {CustomerName} with total {TotalAmount}",
            order.Id,
            order.CustomerName,
            order.TotalAmount
        );
        return order;
    }
}

public sealed class GetOrderHandler(NpgsqlDataSource db, RandomProblems problems)
    : IQueryHandler<GetOrder, Order?>
{
    public async ValueTask<Order?> Handle(GetOrder query, CancellationToken ct)
    {
        using var activity = LabTelemetry.Source.StartActivity(nameof(GetOrderHandler));
        activity?.SetTag("order.id", query.Id);

        await using var conn = await db.OpenConnectionAsync(ct);
        await problems.MaybeDatabaseProblemAsync(conn, ct);
        return await conn.QuerySingleOrDefaultAsync<Order>(
            new CommandDefinition(
                OrderSql.Select + " WHERE id = @Id",
                new { query.Id },
                cancellationToken: ct
            )
        );
    }
}

public sealed class GetOrdersHandler(NpgsqlDataSource db, RandomProblems problems)
    : IQueryHandler<GetOrders, IReadOnlyList<Order>>
{
    public async ValueTask<IReadOnlyList<Order>> Handle(GetOrders query, CancellationToken ct)
    {
        using var activity = LabTelemetry.Source.StartActivity(nameof(GetOrdersHandler));

        await using var conn = await db.OpenConnectionAsync(ct);
        await problems.MaybeDatabaseProblemAsync(conn, ct);
        var orders = await conn.QueryAsync<Order>(
            new CommandDefinition(
                OrderSql.Select
                    + " WHERE (@Status::text IS NULL OR status = @Status) ORDER BY created_at DESC LIMIT @Limit",
                new { Status = query.Status?.ToString(), query.Limit },
                cancellationToken: ct
            )
        );

        var list = orders.AsList();
        activity?.SetTag("orders.count", list.Count);
        return list;
    }
}

public sealed class ChangeOrderStatusHandler(
    NpgsqlDataSource db,
    RandomProblems problems,
    ILogger<ChangeOrderStatusHandler> logger
) : ICommandHandler<ChangeOrderStatus, Order?>
{
    public async ValueTask<Order?> Handle(ChangeOrderStatus command, CancellationToken ct)
    {
        using var activity = LabTelemetry.Source.StartActivity(nameof(ChangeOrderStatusHandler));
        activity?.SetTag("order.id", command.Id);
        activity?.SetTag("order.status", command.Status.ToString());

        await using var conn = await db.OpenConnectionAsync(ct);
        await problems.MaybeDatabaseProblemAsync(conn, ct);
        var order = await conn.QuerySingleOrDefaultAsync<Order>(
            new CommandDefinition(
                "UPDATE orders SET status = @Status WHERE id = @Id RETURNING id, customer_name, total_amount, created_at, status",
                new { command.Id, Status = command.Status.ToString() },
                cancellationToken: ct
            )
        );

        if (order is null)
        {
            logger.LogWarning(
                "Order {OrderId} not found, status change to {OrderStatus} skipped",
                command.Id,
                command.Status
            );
            return null;
        }

        LabTelemetry.OrderStatusChanges.Add(
            1,
            new KeyValuePair<string, object?>("status", command.Status.ToString())
        );
        logger.LogInformation(
            "Order {OrderId} status changed to {OrderStatus}",
            order.Id,
            order.Status
        );
        return order;
    }
}

internal static class OrderSql
{
    public const string Select =
        "SELECT id, customer_name, total_amount, created_at, status FROM orders";
}
