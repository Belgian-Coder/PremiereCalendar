namespace PremiereCalendar.Services;

public interface IAppStateStore
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken);

    Task DeleteValueAsync(string key, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken);
}
