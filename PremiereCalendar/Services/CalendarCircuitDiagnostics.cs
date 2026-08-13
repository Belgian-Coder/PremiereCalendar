using Microsoft.AspNetCore.Components.Server.Circuits;

namespace PremiereCalendar.Services;

public sealed class CalendarCircuitDiagnostics : CircuitHandler
{
    private readonly ILogger<CalendarCircuitDiagnostics> _logger;
    private readonly PremiereTelemetry _telemetry;

    public CalendarCircuitDiagnostics(ILogger<CalendarCircuitDiagnostics> logger, PremiereTelemetry? telemetry = null)
    {
        _logger = logger;
        _telemetry = telemetry ?? new PremiereTelemetry();
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Blazor circuit opened.");
        _telemetry.RecordCircuitEvent("connected");
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Blazor circuit closed.");
        _telemetry.RecordCircuitEvent("closed");
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Blazor circuit connection resumed.");
        _telemetry.RecordCircuitEvent("reconnected");
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Blazor circuit connection disconnected.");
        _telemetry.RecordCircuitEvent("disconnected");
        return Task.CompletedTask;
    }
}
