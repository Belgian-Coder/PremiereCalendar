namespace PremiereCalendar.Services;

public interface IAppStateStore
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken);

    Task DeleteValueAsync(string key, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken);

    async Task ReplaceValuesByPrefixAsync(
        IReadOnlyList<string> prefixes,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        foreach (var prefix in prefixes)
        {
            var existing = await GetValuesByPrefixAsync(prefix, cancellationToken);
            foreach (var key in existing.Keys)
            {
                if (!values.ContainsKey(key))
                {
                    await DeleteValueAsync(key, cancellationToken);
                }
            }
        }

        foreach (var entry in values)
        {
            await SetValueAsync(entry.Key, entry.Value, cancellationToken);
        }
    }
}
