namespace PremiereCalendar.Services;

public enum ProviderCacheScope
{
    Global = 0,
    Week = 1,
    Item = 2
}

public sealed record ProviderCacheState(
    string Provider,
    ProviderCacheScope Scope,
    string Key,
    DateTimeOffset LastCheckedUtc,
    DateTimeOffset? LastChangedUtc,
    string? Watermark,
    int? ItemCount,
    string? MetadataJson);

public interface IProviderCacheStateStore
{
    Task<ProviderCacheState?> GetAsync(
        string provider,
        ProviderCacheScope scope,
        string key,
        CancellationToken cancellationToken);

    Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken);

    async Task SaveManyAsync(IEnumerable<ProviderCacheState> states, CancellationToken cancellationToken)
    {
        foreach (var state in states)
        {
            await SaveAsync(state, cancellationToken);
        }
    }
}
