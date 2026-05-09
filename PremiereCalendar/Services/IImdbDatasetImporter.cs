namespace PremiereCalendar.Services;

public interface IImdbDatasetImporter
{
    Task<int> ImportRatingsAsync(CancellationToken cancellationToken);
}
