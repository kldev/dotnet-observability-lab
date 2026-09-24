using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ObservabilityLab.Api;
using ObservabilityLab.Endpoints.Orders;
using ObservabilityLab.Orders;
using static ObservabilityLab.Endpoints.Orders.Maps.MapChangeStatus;
using static ObservabilityLab.Endpoints.Orders.Maps.MapCreate;

namespace ObservabilityLab.Tests;

public sealed class OrdersApiTests(LabFactory factory) : IClassFixture<LabFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Create_order_returns_201_with_created_order()
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Orders.Create,
            new CreateOrderRequest("  Anna Nowak ", 123.45m)
        );

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(Json);
        Assert.NotNull(order);
        Assert.Equal("Anna Nowak", order.CustomerName);
        Assert.Equal(123.45m, order.TotalAmount);
        Assert.Equal(OrderStatus.Created, order.Status);
        Assert.Equal(ApiRoutes.Orders.For(order.Id), response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("", 10)]
    [InlineData("Jan", 0)]
    [InlineData("Jan", -5)]
    [InlineData("   ", 10)]
    public async Task Create_order_with_invalid_data_returns_400(
        string customerName,
        decimal totalAmount
    )
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Orders.Create,
            new { customerName, totalAmount }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_order_returns_stored_order()
    {
        var created = await CreateOrderAsync("Jan Kowalski", 10m);

        var order = await _client.GetFromJsonAsync<OrderResponse>(
            ApiRoutes.Orders.For(created.Id),
            Json
        );

        Assert.NotNull(order);
        Assert.Equal(created.Id, order.Id);
        Assert.Equal("Jan Kowalski", order.CustomerName);
        Assert.Equal(10m, order.TotalAmount);
    }

    [Fact]
    public async Task Get_unknown_order_returns_404()
    {
        var response = await _client.GetAsync(ApiRoutes.Orders.For(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Change_status_updates_order_and_list_filters_by_status()
    {
        var created = await CreateOrderAsync("Contoso", 99.99m);

        var response = await _client.PutAsJsonAsync(
            ApiRoutes.Orders.StatusFor(created.Id),
            new { status = "completed" }
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<OrderResponse>(Json);
        Assert.Equal(OrderStatus.Completed, updated?.Status);

        var completed = await _client.GetFromJsonAsync<SliceResponse<OrderResponse>>(
            $"{ApiRoutes.Orders.Slice}?status=Completed",
            Json
        );
        Assert.NotNull(completed);
        Assert.Contains(completed.Items, o => o.Id == created.Id);
    }

    [Theory]
    [InlineData("Shipped")]
    [InlineData("7")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Change_status_to_unknown_value_returns_400(string? status)
    {
        var created = await CreateOrderAsync("Ewa", 1m);

        var response = await _client.PutAsJsonAsync(
            ApiRoutes.Orders.StatusFor(created.Id),
            new { status }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Change_status_of_unknown_order_returns_404()
    {
        var response = await _client.PutAsJsonAsync(
            ApiRoutes.Orders.StatusFor(Guid.NewGuid()),
            new { status = "Paid" }
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("?status=7")]
    [InlineData("?status=Shipped")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=501")]
    [InlineData("?page=0")]
    public async Task List_orders_with_invalid_query_returns_400(string query)
    {
        var response = await _client.GetAsync(ApiRoutes.Orders.Slice + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_orders_pages_newest_first()
    {
        var older = await CreateOrderAsync("Paging older", 1m);
        var newer = await CreateOrderAsync("Paging newer", 2m);

        var first = await _client.GetFromJsonAsync<SliceResponse<OrderResponse>>(
            $"{ApiRoutes.Orders.Slice}?page=1&pageSize=1",
            Json
        );
        var second = await _client.GetFromJsonAsync<SliceResponse<OrderResponse>>(
            $"{ApiRoutes.Orders.Slice}?page=2&pageSize=1",
            Json
        );

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.HasMore);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        // Other tests add orders concurrently, so only the relative order of this test's orders is asserted.
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.True(first.Items[0].CreatedAt >= second.Items[0].CreatedAt);
        Assert.NotEqual(older.Id, newer.Id);
    }

    [Fact]
    public async Task OpenApi_document_describes_orders_and_hides_diagnostics()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("\"operationId\": \"Create order\"", document);
        Assert.Contains("\"operationId\": \"Get orders\"", document);
        Assert.Contains("Sales - Orders", document);
        Assert.DoesNotContain(ApiRoutes.Diagnostics.Problem, document);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"Healthy\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_http_and_domain_metrics()
    {
        await CreateOrderAsync("Metrics", 5m);

        var metrics = await _client.GetStringAsync("/metrics");

        Assert.Contains("lab_orders_created_total", metrics);
        Assert.Contains("http_server_request_duration_seconds", metrics);
    }

    [Theory]
    [InlineData(ApiRoutes.Diagnostics.Problem + "/bad-request", HttpStatusCode.BadRequest)]
    [InlineData(ApiRoutes.Diagnostics.Problem + "/not-found", HttpStatusCode.NotFound)]
    [InlineData(ApiRoutes.Diagnostics.Problem + "/error", HttpStatusCode.InternalServerError)]
    [InlineData(ApiRoutes.Diagnostics.Problem + "/exception", HttpStatusCode.InternalServerError)]
    [InlineData(ApiRoutes.Diagnostics.Problem + "/db-error", HttpStatusCode.InternalServerError)]
    public async Task Problem_endpoints_return_expected_status(string path, HttpStatusCode expected)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
    }

    private async Task<OrderResponse> CreateOrderAsync(string customerName, decimal totalAmount)
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Orders.Create,
            new CreateOrderRequest(customerName, totalAmount)
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderResponse>(Json)
            ?? throw new InvalidOperationException("Empty response body");
    }
}
