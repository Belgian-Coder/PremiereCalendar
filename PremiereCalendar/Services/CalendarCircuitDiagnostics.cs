using Microsoft.AspNetCore.Components.Server.Circuits;

namespace PremiereCalendar.Services;

public sealed class CalendarCircuitDiagnostics : CircuitHandler
{
    private readonly ILogger<CalendarCircuitDiagnostics> _logger;

    public CalendarCircuitDiagnostics(ILogger<CalendarCircuitDiagnostics> logger)
    {
        _logger = logger;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Blazor circuit opened: {CircuitId}.", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Blazor circuit closed: {CircuitId}.", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Blazor circuit connection resumed: {CircuitId}.", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Blazor circuit connection disconnected: {CircuitId}.", circuit.Id);
        return Task.CompletedTask;
    }
}
