namespace PremiereCalendar.Services;

public interface IWikimediaClient
{
    Task<string?> GetReusableImageUrlAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<string?> GetRottenTomatoesIdAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    async Task<WikimediaIdentifierResult> GetRottenTomatoesIdResultAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var value = await GetRottenTomatoesIdAsync(wikidataId, cancellationToken, forceRefresh);
        return new(value, IsSuccess: !string.IsNullOrWhiteSpace(value));
    }
}

public sealed record WikimediaIdentifierResult(string? Value, bool IsSuccess);
