namespace ObservabilityLab.Api;

/// <summary>All HTTP routes in one place - endpoints, middleware and tests use the same constants.</summary>
internal static class ApiRoutes
{
    private const string Base = "/api";

    internal static class Orders
    {
        public const string Base = $"{ApiRoutes.Base}/orders";

        public const string Create = Base;
        public const string Slice = Base;
        public const string Get = $"{Base}/{{orderId:guid}}";
        public const string ChangeStatus = $"{Base}/{{orderId:guid}}/status";

        public static string For(Guid orderId) => $"{Base}/{orderId}";

        public static string StatusFor(Guid orderId) => $"{Base}/{orderId}/status";
    }

    /// <summary>Lab-only endpoints that break things on purpose (mapped only when diagnostics are enabled).</summary>
    internal static class Diagnostics
    {
        public const string Problem = "/diagnostics/problem";
        public const string RandomProblems = "/diagnostics/random-problems";
    }
}
