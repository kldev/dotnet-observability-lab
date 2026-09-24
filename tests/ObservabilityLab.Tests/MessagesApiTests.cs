using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ObservabilityLab.Api;
using ObservabilityLab.Endpoints.Messages;
using ObservabilityLab.Messaging;
using RabbitMQ.Client;
using static ObservabilityLab.Endpoints.Messages.Maps.MapPublish;
using static ObservabilityLab.Endpoints.Messages.Maps.MapPublishBurst;

namespace ObservabilityLab.Tests;

public sealed class MessagesApiTests(LabFactory factory) : IClassFixture<LabFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Published_message_is_confirmed_and_then_acked_by_the_consumer()
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Messages.Publish,
            new PublishMessageRequest("  hello rabbit ")
        );

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var published = await response.Content.ReadFromJsonAsync<PublishedMessageResponse>(Json);
        Assert.NotNull(published);

        var received = await WaitForReceivedAsync(published.Id);
        Assert.Equal("hello rabbit", received.Text);
        Assert.Equal(MessageOutcome.Acked, received.Outcome);
    }

    [Fact]
    public async Task Failing_message_ends_in_the_dead_letter_queue()
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Messages.Publish,
            new PublishMessageRequest("poison", Fail: true)
        );
        var published = await response.Content.ReadFromJsonAsync<PublishedMessageResponse>(Json);

        var received = await WaitForReceivedAsync(published!.Id);
        Assert.Equal(MessageOutcome.DeadLettered, received.Outcome);

        var queue = await _client.GetFromJsonAsync<QueueStatusResponse>(
            ApiRoutes.Messages.Queue,
            Json
        );
        Assert.NotNull(queue);
        Assert.True(queue.DeadLettered >= 1);
        Assert.Equal(1u, queue.Consumers);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("ok", -1)]
    [InlineData("ok", 60_001)]
    public async Task Publish_with_invalid_data_returns_400(string text, int processingMs)
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Messages.Publish,
            new { text, processingMs }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Burst_publishes_every_message_from_many_threads()
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Messages.PublishBurst,
            new PublishBurstRequest(Count: 500, Parallelism: 32)
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var burst = await response.Content.ReadFromJsonAsync<PublishBurstResponse>(Json);
        Assert.NotNull(burst);
        Assert.Equal(500, burst.Published);
        Assert.Equal(0, burst.Failed);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(10, 0)]
    [InlineData(10, 65)]
    public async Task Burst_with_invalid_data_returns_400(int count, int parallelism)
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Messages.PublishBurst,
            new { count, parallelism }
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Channel_pool_never_lends_one_channel_to_two_callers_at_once()
    {
        var pool = factory.Services.GetRequiredService<PublisherChannelPool>();
        var size = factory
            .Services.GetRequiredService<IOptions<RabbitMqOptions>>()
            .Value.PublisherChannels;
        var inUse = new ConcurrentDictionary<IChannel, byte>(ReferenceEqualityComparer.Instance);
        var seen = new ConcurrentDictionary<IChannel, byte>(ReferenceEqualityComparer.Instance);
        var maxConcurrent = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 400),
            new ParallelOptions { MaxDegreeOfParallelism = 50 },
            async (_, ct) =>
            {
                await using var lease = await pool.RentAsync(ct);
                Assert.True(inUse.TryAdd(lease.Channel, 0), "channel lent to two callers at once");
                seen.TryAdd(lease.Channel, 0);
                InterlockedMax(ref maxConcurrent, inUse.Count);
                await Task.Delay(1, ct);
                inUse.TryRemove(lease.Channel, out byte _);
            }
        );

        Assert.True(maxConcurrent <= size, $"{maxConcurrent} channels in use, pool size {size}");
        // Other tests share the pool, so a channel may have been replaced - but not one per rent.
        Assert.InRange(seen.Count, 1, size * 2);
    }

    [Fact]
    public async Task Received_with_invalid_limit_returns_400()
    {
        var response = await _client.GetAsync($"{ApiRoutes.Messages.Received}?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_messages()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("\"operationId\": \"Publish message\"", document);
        Assert.Contains("\"operationId\": \"Publish message burst\"", document);
        Assert.Contains("\"operationId\": \"Get received messages\"", document);
        Assert.Contains("\"operationId\": \"Get queue status\"", document);
        Assert.Contains("Messaging - Messages", document);
    }

    [Fact]
    public async Task Metrics_endpoint_exposes_messaging_metrics()
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Messages.Publish,
            new PublishMessageRequest("metrics")
        );
        var published = await response.Content.ReadFromJsonAsync<PublishedMessageResponse>(Json);
        await WaitForReceivedAsync(published!.Id);

        var metrics = await _client.GetStringAsync("/metrics");

        Assert.Contains("lab_messages_published_total", metrics);
        Assert.Contains("lab_messages_consumed_total", metrics);
        Assert.Contains("lab_messages_publish_duration_seconds_bucket", metrics);
        Assert.Contains("lab_rabbitmq_publisher_channel_wait_seconds_bucket", metrics);
    }

    private async Task<ReceivedMessageResponse> WaitForReceivedAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var received = await _client.GetFromJsonAsync<ReceivedMessageResponse[]>(
                $"{ApiRoutes.Messages.Received}?limit=200",
                Json,
                timeout.Token
            );
            if (received?.FirstOrDefault(m => m.Id == id) is { } message)
                return message;
            await Task.Delay(100, timeout.Token);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }
}
