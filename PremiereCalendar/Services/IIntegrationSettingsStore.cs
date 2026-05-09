using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IIntegrationSettingsStore
{
    Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default);
}
