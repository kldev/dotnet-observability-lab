namespace ObservabilityLab.Api;

/// <summary>One page of a collection. <c>HasMore</c> says whether the next page has items (no total count on purpose).</summary>
public sealed record SliceResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasMore);
