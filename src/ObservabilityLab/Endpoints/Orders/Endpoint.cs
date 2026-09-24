using ObservabilityLab.Api;

namespace ObservabilityLab.Endpoints.Orders;

public static class Endpoint
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("").WithTags(ApiTags.Orders);

        Maps.MapCreate.Map(group);
        Maps.MapGetSlice.Map(group);
        Maps.MapGet.Map(group);
        Maps.MapChangeStatus.Map(group);
    }
}
