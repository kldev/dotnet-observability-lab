using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ObservabilityLab.Api;
using ObservabilityLab.Endpoints.Emails;
using ObservabilityLab.Endpoints.Messages;
using ObservabilityLab.Messaging;
using static ObservabilityLab.Endpoints.Emails.Maps.MapPublish;

namespace ObservabilityLab.Tests;

public sealed class EmailsApiTests(LabFactory factory) : IClassFixture<LabFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData(
        EmailDepartment.Recruitment,
        EmailPriority.Normal,
        "recruitment.normal",
        new[] { "q.emails.recruitment", "q.emails.audit" }
    )]
    [InlineData(
        EmailDepartment.Sales,
        EmailPriority.Urgent,
        "sales.urgent",
        new[] { "q.emails.sales", "q.emails.urgent", "q.emails.audit" }
    )]
    [InlineData(
        EmailDepartment.Support,
        EmailPriority.Urgent,
        "support.urgent",
        new[] { "q.emails.support", "q.emails.urgent", "q.emails.audit" }
    )]
    public async Task Email_reaches_exactly_the_queues_its_routing_key_matches(
        EmailDepartment department,
        EmailPriority priority,
        string routingKey,
        string[] expectedQueues
    )
    {
        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Emails.Publish,
            new PublishEmailRequest(department, " Offer ", priority),
            Json
        );

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var published = await response.Content.ReadFromJsonAsync<PublishedEmailResponse>(Json);
        Assert.NotNull(published);
        Assert.Equal("x.emails", published.Exchange);
        Assert.Equal(routingKey, published.RoutingKey);
        Assert.Equal(expectedQueues.Order(), published.Queues.Order());

        // What the broker really delivered - one copy per matching queue, none elsewhere.
        var received = await WaitForCopiesAsync(published.Id, expectedQueues.Length);
        await Task.Delay(300); // give a wrongly routed extra copy the chance to show up
        received = await ReceivedAsync(published.Id);
        Assert.Equal(expectedQueues.Order(), received.Select(r => r.Queue).Order());
        Assert.All(received, r => Assert.Equal(routingKey, r.RoutingKey));
        Assert.All(received, r => Assert.Equal("Offer", r.Text));
    }

    [Fact]
    public async Task Slow_queue_does_not_hold_up_the_other_queues()
    {
        // 8 slow support emails keep q.emails.support and q.emails.audit busy for a few seconds
        // (8 x 1.5 s at concurrency 4 = 3 s each).
        await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ =>
                    _client.PostAsJsonAsync(
                        ApiRoutes.Emails.Publish,
                        new PublishEmailRequest(
                            EmailDepartment.Support,
                            "slow",
                            ProcessingMs: 1500
                        ),
                        Json
                    )
                )
        );

        var response = await _client.PostAsJsonAsync(
            ApiRoutes.Emails.Publish,
            new PublishEmailRequest(EmailDepartment.Sales, "fast"),
            Json
        );
        var published = await response.Content.ReadFromJsonAsync<PublishedEmailResponse>(Json);

        // q.emails.sales has a channel of its own, so its copy is processed right away.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ReceivedMessageResponse? sales = null;
        while (sales is null)
        {
            sales = (await ReceivedAsync(published!.Id, timeout.Token)).FirstOrDefault(r =>
                r.Queue == "q.emails.sales"
            );
            if (sales is null)
                await Task.Delay(50, timeout.Token);
        }
        Assert.True(
            sales.EndToEndMs < 1000,
            $"sales copy took {sales.EndToEndMs} ms behind the slow support queue"
        );
    }

    [Theory]
    [InlineData("recruitment.*", "recruitment.normal", true)]
    [InlineData("recruitment.*", "recruitment", false)]
    [InlineData("recruitment.*", "recruitment.normal.extra", false)]
    [InlineData("*.urgent", "sales.urgent", true)]
    [InlineData("*.urgent", "sales.normal", false)]
    [InlineData("#", "anything.at.all", true)]
    [InlineData("#", "", true)]
    [InlineData("sales.#", "sales", true)]
    [InlineData("sales.#", "sales.eu.urgent", true)]
    [InlineData("#.urgent", "sales.eu.urgent", true)]
    [InlineData("#.urgent", "sales.urgent.no", false)]
    public void Topic_matching_follows_amqp_rules(
        string pattern,
        string routingKey,
        bool matches
    ) => Assert.Equal(matches, MessagingTopology.TopicMatches(pattern, routingKey));

    [Fact]
    public async Task Email_queues_are_listed_with_bindings_and_a_consumer_each()
    {
        var queues = await _client.GetFromJsonAsync<EmailQueueResponse[]>(
            ApiRoutes.Emails.Queues,
            Json
        );

        Assert.NotNull(queues);
        Assert.Equal(
            MessagingTopology.EmailQueues.Select(q => (q.Queue, q.Binding)),
            queues.Select(q => (q.Queue, q.Binding))
        );
        Assert.All(queues, q => Assert.Equal(1u, q.Consumers));
    }

    [Theory]
    [InlineData("""{"department":"Marketing","subject":"x"}""")]
    [InlineData("""{"department":"Sales","subject":""}""")]
    [InlineData("""{"department":"Sales","subject":"x","priority":"Low"}""")]
    [InlineData("""{"subject":"x"}""")]
    public async Task Publish_email_with_invalid_data_returns_400(string body)
    {
        var response = await _client.PostAsync(
            ApiRoutes.Emails.Publish,
            new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        );

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_describes_emails()
    {
        var document = await _client.GetStringAsync("/openapi/v1.json");

        Assert.Contains("\"operationId\": \"Publish email\"", document);
        Assert.Contains("\"operationId\": \"Get email queues\"", document);
        Assert.Contains("Messaging - Emails", document);
    }

    private async Task<ReceivedMessageResponse[]> WaitForCopiesAsync(Guid id, int copies)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var received = await ReceivedAsync(id, timeout.Token);
            if (received.Length >= copies)
                return received;
            await Task.Delay(100, timeout.Token);
        }
    }

    private async Task<ReceivedMessageResponse[]> ReceivedAsync(
        Guid id,
        CancellationToken ct = default
    )
    {
        var received = await _client.GetFromJsonAsync<ReceivedMessageResponse[]>(
            $"{ApiRoutes.Messages.Received}?limit=200",
            Json,
            ct
        );
        return [.. (received ?? []).Where(m => m.Id == id)];
    }
}
