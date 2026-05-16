using System.Text.Json;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class CalendarPresetService
{
    private const string StoreKey = "Calendar.FilterPresets";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IAppStateStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CalendarPresetService(IAppStateStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<CalendarFilterPreset>> GetPresetsAsync(
        CalendarPageMode pageMode,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var presets = await LoadAsync(cancellationToken);
            return presets
                .Where(preset => preset.PageMode == pageMode)
                .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CalendarFilterPreset> SaveAsync(
        string name,
        CalendarPageMode pageMode,
        CalendarFilters filters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var trimmedName = string.IsNullOrWhiteSpace(name) ? "Saved filters" : name.Trim();
            var presets = await LoadAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            var snapshot = CreatePresetSnapshot(filters, pageMode);
            var existingIndex = presets.FindIndex(preset =>
                preset.PageMode == pageMode && string.Equals(preset.Name, trimmedName, StringComparison.CurrentCultureIgnoreCase));
            var preset = existingIndex >= 0
                ? presets[existingIndex] with { Filters = snapshot, UpdatedUtc = now, Name = trimmedName }
                : new CalendarFilterPreset(Guid.NewGuid().ToString("N"), trimmedName, pageMode, snapshot, now, now);

            if (existingIndex >= 0)
            {
                presets[existingIndex] = preset;
            }
            else
            {
                presets.Add(preset);
            }

            await SaveAllAsync(presets, cancellationToken);
            return preset;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var presets = await LoadAsync(cancellationToken);
            presets.RemoveAll(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));
            await SaveAllAsync(presets, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CalendarFilters CreatePresetSnapshot(CalendarFilters filters, CalendarPageMode pageMode)
    {
        var snapshot = CalendarFilterState.Clone(filters);
        snapshot.WeekStart = DateOnly.MinValue;
        snapshot.PriorityDate = null;
        CalendarFilterState.ApplyPageMode(snapshot, pageMode);
        CalendarFilterState.Normalize(snapshot);
        return snapshot;
    }

    private async Task<List<CalendarFilterPreset>> LoadAsync(CancellationToken cancellationToken)
    {
        var json = await _store.GetValueAsync(StoreKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CalendarFilterPreset>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private Task SaveAllAsync(List<CalendarFilterPreset> presets, CancellationToken cancellationToken)
    {
        var ordered = presets
            .OrderBy(preset => preset.PageMode)
            .ThenBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return _store.SetValueAsync(StoreKey, JsonSerializer.Serialize(ordered, JsonOptions), cancellationToken);
    }
}
