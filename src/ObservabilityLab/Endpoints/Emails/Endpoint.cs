using ObservabilityLab.Api;

namespace ObservabilityLab.Endpoints.Emails;

public static class Endpoint
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("").WithTags(ApiTags.Emails);

        Maps.MapPublish.Map(group);
        Maps.MapGetQueues.Map(group);
    }
}
