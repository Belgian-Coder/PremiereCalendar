using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IArrIntegrationService
{
    Task<ArrAddResult> AddAsync(PremiereItem item, CancellationToken cancellationToken = default);

    Task<ArrConnectionOptions> GetSonarrOptionsAsync(
        SonarrIntegrationSettings settings,
        CancellationToken cancellationToken = default);

    Task<ArrConnectionOptions> GetRadarrOptionsAsync(
        RadarrIntegrationSettings settings,
        CancellationToken cancellationToken = default);
}
