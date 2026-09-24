using ObservabilityLab.Api;

namespace ObservabilityLab.Endpoints.Messages;

public static class Endpoint
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("").WithTags(ApiTags.Messages);

        Maps.MapPublish.Map(group);
        Maps.MapPublishBurst.Map(group);
        Maps.MapGetReceived.Map(group);
        Maps.MapGetQueue.Map(group);
    }
}
