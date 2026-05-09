namespace PremiereCalendar.Services;

public interface IWikimediaClient
{
    Task<string?> GetReusableImageUrlAsync(
        string wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
