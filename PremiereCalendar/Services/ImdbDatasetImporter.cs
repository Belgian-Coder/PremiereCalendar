using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class ImdbDatasetImporter : IImdbDatasetImporter
{
    private readonly HttpClient _httpClient;
    private readonly IImdbRatingsStore _ratingsStore;
    private readonly ImdbDatasetOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImdbDatasetImporter> _logger;

    public ImdbDatasetImporter(
        HttpClient httpClient,
        IImdbRatingsStore ratingsStore,
        IOptions<ImdbDatasetOptions> options,
        TimeProvider timeProvider,
        ILogger<ImdbDatasetImporter> logger)
    {
        _httpClient = httpClient;
        _ratingsStore = ratingsStore;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> ImportRatingsAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return 0;
        }

        var importedAtUtc = _timeProvider.GetUtcNow();
        try
        {
            using var response = await _httpClient.GetAsync(_options.RatingsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var gzip = new GZipStream(responseStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            var records = new List<ImdbRatingRecord>();
            var isHeader = true;

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (isHeader)
                {
                    isHeader = false;
                    continue;
                }

                var parts = line.Split('\t');
                if (parts.Length < 3
                    || string.IsNullOrWhiteSpace(parts[0])
                    || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var averageRating)
                    || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var voteCount))
                {
                    continue;
                }

                records.Add(new ImdbRatingRecord(parts[0].Trim(), averageRating, voteCount, importedAtUtc));
            }

            await _ratingsStore.ReplaceAllAsync(records, importedAtUtc, cancellationToken);
            _logger.LogInformation("Imported {Count} IMDb title ratings.", records.Count);
            return records.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "IMDb ratings dataset import failed.");
            var state = await _ratingsStore.GetStateAsync(cancellationToken);
            await _ratingsStore.SaveStateAsync(
                state with { LastError = ex.Message },
                cancellationToken);
            throw;
        }
    }
}
