using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.Components.Pages;

public partial class Settings
{
    private IntegrationSettings _settings = new();
    private ArrConnectionOptions _sonarrOptions = new([], []);
    private ArrConnectionOptions _radarrOptions = new([], []);
    private readonly List<ToastMessage> _toasts = [];
    private string _tvmazeScheduleCountries = "";
    private string _watchmodeRegions = "";
    private bool _isLoading = true;
    private bool _isSaving;
    private bool _isLoadingSonarr;
    private bool _isLoadingRadarr;
    private bool _isRequestingSimklPin;
    private bool _isCheckingSimklPin;
    private SimklPinCodeResult? _simklPinCode;
    private string? _simklAuthorizationStatus;
    private string? _loadError;
    private string? _tmdbReturnUrl;
    private CancellationTokenSource? _simklPinPollingCancellation;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private EditContext? _editContext;
    private bool _isDirty;
    private bool _viewSyncLoaded;
    private bool _viewSyncEnabled;
    private bool _isSavingViewSync;
    private bool _isCreatingViewSyncGroup;
    private bool _isUngroupingViewSync;
    private bool _showTmdbRequiredNotice;
    private string _viewSyncDeviceId = "";
    private string _viewSyncDeviceName = "This browser";
    private string? _selectedViewSyncGroupId;
    private string _newViewSyncGroupName = "";
    private ViewSyncOverview? _viewSyncOverview;
    private IReadOnlyList<ViewSyncGroup> _viewSyncGroups = [];
    private IReadOnlyList<ViewSyncDevice> _viewSyncGroupDevices = [];
    private IReadOnlyList<ViewSyncGroupState> _viewSyncGroupStates = [];
    private IReadOnlyList<ViewSyncGroupOverview> _viewSyncGroupOverviews = [];
    private CacheInspectorSummary? _cacheSummary;
    private IReadOnlyList<BackgroundJobEvent> _jobEvents = [];
    private SourceHealthOverview? _sourceHealth;
    private SystemStatusSnapshot? _systemStatus;
    private bool _isBackfillingScores;
    private bool _isRepairingExternalIds;
    private string? _maintenanceResult;
    private string _backupJson = "";
    private bool _includeBackupSecrets;

    // Stored secrets are never rendered. Values typed into the write-only secret inputs live here
    // until Save replaces the stored value; a blank input keeps whatever is already stored.
    private readonly Dictionary<SecretField, string> _secretDrafts = [];

    private enum SecretField
    {
        SonarrApiKey,
        RadarrApiKey,
        TmdbBearerToken,
        TraktClientId,
        WatchmodeApiKey,
        SimklClientId,
        SimklClientSecret,
        SimklAccessToken,
        OmdbApiKey,
        FanartApiKey,
        TheTvdbApiKey
    }

    private string StoredSecret(SecretField field) => field switch
    {
        SecretField.SonarrApiKey => _settings.Sonarr.ApiKey,
        SecretField.RadarrApiKey => _settings.Radarr.ApiKey,
        SecretField.TmdbBearerToken => _settings.Sources.Tmdb.BearerToken,
        SecretField.TraktClientId => _settings.Sources.Trakt.ClientId,
        SecretField.WatchmodeApiKey => _settings.Sources.Watchmode.ApiKey,
        SecretField.SimklClientId => _settings.Sources.Simkl.ClientId,
        SecretField.SimklClientSecret => _settings.Sources.Simkl.ClientSecret,
        SecretField.SimklAccessToken => _settings.Sources.Simkl.AccessToken,
        SecretField.OmdbApiKey => _settings.Sources.Omdb.ApiKey,
        SecretField.FanartApiKey => _settings.Sources.Fanart.ApiKey,
        SecretField.TheTvdbApiKey => _settings.Sources.TheTvdb.ApiKey,
        _ => ""
    };

    private void StoreSecret(SecretField field, string value)
    {
        switch (field)
        {
            case SecretField.SonarrApiKey: _settings.Sonarr.ApiKey = value; break;
            case SecretField.RadarrApiKey: _settings.Radarr.ApiKey = value; break;
            case SecretField.TmdbBearerToken: _settings.Sources.Tmdb.BearerToken = value; break;
            case SecretField.TraktClientId: _settings.Sources.Trakt.ClientId = value; break;
            case SecretField.WatchmodeApiKey: _settings.Sources.Watchmode.ApiKey = value; break;
            case SecretField.SimklClientId: _settings.Sources.Simkl.ClientId = value; break;
            case SecretField.SimklClientSecret: _settings.Sources.Simkl.ClientSecret = value; break;
            case SecretField.SimklAccessToken: _settings.Sources.Simkl.AccessToken = value; break;
            case SecretField.OmdbApiKey: _settings.Sources.Omdb.ApiKey = value; break;
            case SecretField.FanartApiKey: _settings.Sources.Fanart.ApiKey = value; break;
            case SecretField.TheTvdbApiKey: _settings.Sources.TheTvdb.ApiKey = value; break;
        }
    }

    private string _persistedSonarrBaseUrl = "";
    private string _persistedRadarrBaseUrl = "";

    // Persistent (until dismissed) notices: a removed Arr key must not vanish with the 3 second toast.
    private readonly List<string> _keyNotices = [];

    private void AddKeyNotice(string notice)
    {
        if (!_keyNotices.Contains(notice))
        {
            _keyNotices.Add(notice);
        }
    }

    private void DismissKeyNotice(string notice) => _keyNotices.Remove(notice);

    private bool HasSecretDraft(SecretField field)
    {
        return _secretDrafts.TryGetValue(field, out var draft) && !string.IsNullOrWhiteSpace(draft);
    }

    /// <summary>True when an Arr base URL was edited away from the saved one, so the saved key must not follow it.</summary>
    private bool ArrUrlChanged(SecretField field) => field switch
    {
        SecretField.SonarrApiKey => !SecretHostBinding.SameBaseUrl(_settings.Sonarr.BaseUrl, _persistedSonarrBaseUrl),
        SecretField.RadarrApiKey => !SecretHostBinding.SameBaseUrl(_settings.Radarr.BaseUrl, _persistedRadarrBaseUrl),
        _ => false
    };

    private void RememberPersistedArrUrls()
    {
        _persistedSonarrBaseUrl = _settings.Sonarr.BaseUrl ?? "";
        _persistedRadarrBaseUrl = _settings.Radarr.BaseUrl ?? "";
    }

    /// <summary>The value a connection test should use: a newly typed secret, otherwise the stored one (never for a changed Arr URL).</summary>
    private string EffectiveSecret(SecretField field)
    {
        if (HasSecretDraft(field))
        {
            return _secretDrafts[field].Trim();
        }

        return ArrUrlChanged(field) ? "" : StoredSecret(field);
    }

    private Dictionary<SecretField, string> SnapshotSecrets()
    {
        return Enum.GetValues<SecretField>().ToDictionary(field => field, StoredSecret);
    }

    private void RestoreSecrets(Dictionary<SecretField, string> snapshot)
    {
        foreach (var (field, value) in snapshot)
        {
            StoreSecret(field, value);
        }
    }

    /// <summary>Writes typed secrets into the settings and drops a saved Arr key whose URL changed without a new key.</summary>
    private List<string> ApplyPendingSecrets()
    {
        var notices = new List<string>();
        foreach (var (field, name) in new[] { (SecretField.SonarrApiKey, "Sonarr"), (SecretField.RadarrApiKey, "Radarr") })
        {
            if (ArrUrlChanged(field) && !HasSecretDraft(field) && !string.IsNullOrWhiteSpace(StoredSecret(field)))
            {
                StoreSecret(field, "");
                notices.Add($"The {name} URL changed, so its saved API key was removed. Enter the API key again.");
            }
        }

        foreach (var (field, draft) in _secretDrafts)
        {
            if (!string.IsNullOrWhiteSpace(draft))
            {
                StoreSecret(field, draft.Trim());
            }
        }

        return notices;
    }

    /// <summary>Saves the settings with pending secrets; drafts only leave the pending state once the save succeeded.</summary>
    private async Task<List<string>> PersistSettingsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = SnapshotSecrets();
        var notices = ApplyPendingSecrets();
        try
        {
            NormalizeSettings();
            await SettingsStore.SaveAsync(_settings, cancellationToken);
        }
        catch
        {
            RestoreSecrets(snapshot);
            throw;
        }

        _secretDrafts.Clear();
        RememberPersistedArrUrls();
        return notices;
    }

    private void OnSecretChanged(SecretField field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _secretDrafts.Remove(field);
            return;
        }

        _secretDrafts[field] = value;
        _isDirty = true;
    }

    private void ClearSecret(SecretField field)
    {
        _secretDrafts.Remove(field);
        StoreSecret(field, "");
        _isDirty = true;
    }

    private RenderFragment SecretInput(SecretField field, string label, string? id = null) => builder =>
    {
        var hasStored = !string.IsNullOrWhiteSpace(StoredSecret(field));
        var hasDraft = _secretDrafts.ContainsKey(field);

        builder.OpenElement(0, "input");
        if (id is not null)
        {
            builder.AddAttribute(1, "id", id);
        }

        builder.AddAttribute(2, "type", "password");
        builder.AddAttribute(3, "class", "settings-input");
        builder.AddAttribute(4, "aria-label", label);
        builder.AddAttribute(5, "autocomplete", "new-password");
        builder.AddAttribute(6, "placeholder", hasStored ? "Saved - type to replace" : "Not set");
        builder.AddAttribute(7, "value", _secretDrafts.GetValueOrDefault(field, ""));
        builder.AddAttribute(8, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(
            this,
            args => OnSecretChanged(field, args.Value?.ToString())));
        builder.CloseElement();

        if (hasStored && !hasDraft && ArrUrlChanged(field))
        {
            builder.OpenElement(19, "small");
            builder.AddAttribute(20, "class", "settings-help");
            builder.AddAttribute(21, "role", "status");
            builder.AddAttribute(22, "data-testid", $"secret-url-hint-{field}");
            builder.AddContent(23, "URL changed \u2013 enter the API key again.");
            builder.CloseElement();
        }

        builder.OpenElement(9, "small");
        builder.AddAttribute(10, "class", "settings-help");
        builder.AddAttribute(11, "data-testid", $"secret-status-{field}");
        builder.AddContent(12, hasDraft
            ? "New value will replace the saved one when you save."
            : hasStored
                ? "Saved. Leave blank to keep it."
                : "Not set.");
        builder.CloseElement();

        if (hasStored || hasDraft)
        {
            builder.OpenElement(13, "button");
            builder.AddAttribute(14, "type", "button");
            builder.AddAttribute(15, "class", "tool-button compact-command");
            builder.AddAttribute(16, "aria-label", $"Clear {label}");
            builder.AddAttribute(17, "onclick", EventCallback.Factory.Create(this, () => ClearSecret(field)));
            builder.AddContent(18, "Clear");
            builder.CloseElement();
        }
    };

    private bool HasSimklAccessToken => !string.IsNullOrWhiteSpace(_settings.Sources.Simkl.AccessToken);

    private string SimklConnectButtonText => HasSimklAccessToken ? "Reconnect SIMKL" : "Connect SIMKL";

    private string SettingsDirtyText => _isDirty ? "Unsaved changes" : "All changes saved";

    private string SettingsDirtyClass => _isDirty ? "settings-dirty dirty" : "settings-dirty clean";

    private string ViewSyncSaveButtonText
    {
        get
        {
            if (_isSavingViewSync)
            {
                return "Saving...";
            }

            return !_viewSyncEnabled && !string.IsNullOrWhiteSpace(_selectedViewSyncGroupId)
                ? "Add this browser"
                : "Save view sync";
        }
    }

    private string SimklConnectionSummary
    {
        get
        {
            if (_simklPinCode is not null)
            {
                return "Open SIMKL, enter the code, and leave this page open while it saves the token.";
            }

            return HasSimklAccessToken
                ? "Access token saved locally. Reconnect only if SIMKL access stops working."
                : "Connect once to let SIMKL sync state use an OAuth access token.";
        }
    }

    private string SimklStatusText
    {
        get
        {
            if (_simklPinCode is not null)
            {
                return _simklAuthorizationStatus ?? "Waiting for authorization";
            }

            if (!string.IsNullOrWhiteSpace(_simklAuthorizationStatus))
            {
                return _simklAuthorizationStatus;
            }

            return HasSimklAccessToken ? "Connected" : "Not connected";
        }
    }

    private string SimklStatusClass
    {
        get
        {
            if (_simklPinCode is not null)
            {
                return "pending";
            }

            if (!string.IsNullOrWhiteSpace(_simklAuthorizationStatus))
            {
                return "idle";
            }

            return HasSimklAccessToken ? "connected" : "idle";
        }
    }

    private RenderFragment StatusBadge(string name, ProviderAvailability status) => builder =>
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", $"integration-status {status.CssClass}");
        builder.AddContent(2, $"{name}: {status.Text}");
        builder.CloseElement();
    };

    private ProviderAvailability ViewSyncProviderStatus()
    {
        if (!_viewSyncLoaded)
        {
            return new ProviderAvailability("Checking", "configured");
        }

        if (!_viewSyncEnabled)
        {
            return ProviderAvailability.Disabled;
        }

        return string.IsNullOrWhiteSpace(_selectedViewSyncGroupId)
            ? ProviderAvailability.NeedsSetup
            : ProviderAvailability.Online;
    }

    private ProviderAvailability SonarrProviderStatus()
    {
        if (!_settings.Sonarr.Enabled)
        {
            return ProviderAvailability.Disabled;
        }

        if (!CanLoadSonarrOptions())
        {
            return ProviderAvailability.NeedsSetup;
        }

        return HasLoadedOptions(_sonarrOptions) ? ProviderAvailability.Online : ProviderAvailability.Available;
    }

    private ProviderAvailability RadarrProviderStatus()
    {
        if (!_settings.Radarr.Enabled)
        {
            return ProviderAvailability.Disabled;
        }

        if (!CanLoadRadarrOptions())
        {
            return ProviderAvailability.NeedsSetup;
        }

        return HasLoadedOptions(_radarrOptions) ? ProviderAvailability.Online : ProviderAvailability.Available;
    }

    private ProviderAvailability TmdbProviderStatus()
    {
        return string.IsNullOrWhiteSpace(_settings.Sources.Tmdb.BearerToken)
            ? ProviderAvailability.NeedsSetup
            : ProviderAvailability.Available;
    }

    private ProviderAvailability TvmazeProviderStatus()
    {
        return _settings.Sources.Tvmaze.Enabled
            ? ProviderAvailability.Available
            : ProviderAvailability.Disabled;
    }

    private ProviderAvailability TraktProviderStatus()
    {
        return EnabledWithRequiredValue(_settings.Sources.Trakt.Enabled, _settings.Sources.Trakt.ClientId);
    }

    private ProviderAvailability WatchmodeAvailabilityProviderStatus()
    {
        return EnabledWithRequiredValue(
            _settings.Sources.Watchmode.Enabled && _settings.Sources.Watchmode.EnableAvailabilityEnrichment,
            _settings.Sources.Watchmode.ApiKey);
    }

    private ProviderAvailability SimklProviderStatus()
    {
        if (!_settings.Sources.Simkl.Enabled)
        {
            return ProviderAvailability.Disabled;
        }

        if (string.IsNullOrWhiteSpace(_settings.Sources.Simkl.ClientId)
            || string.IsNullOrWhiteSpace(_settings.Sources.Simkl.ClientSecret))
        {
            return ProviderAvailability.NeedsSetup;
        }

        return HasSimklAccessToken ? ProviderAvailability.Online : new ProviderAvailability("Needs auth", "needs-setup");
    }

    private ProviderAvailability OmdbProviderStatus()
    {
        return EnabledWithRequiredValue(_settings.Sources.Omdb.Enabled, _settings.Sources.Omdb.ApiKey);
    }

    private ProviderAvailability FanartProviderStatus()
    {
        return EnabledWithRequiredValue(_settings.Sources.Fanart.Enabled, _settings.Sources.Fanart.ApiKey);
    }

    private ProviderAvailability TheTvdbProviderStatus()
    {
        return EnabledWithRequiredValue(_settings.Sources.TheTvdb.Enabled, _settings.Sources.TheTvdb.ApiKey);
    }

    private ProviderAvailability WikimediaProviderStatus()
    {
        return _settings.Sources.Wikimedia.Enabled
            ? ProviderAvailability.Available
            : ProviderAvailability.Disabled;
    }

    private static ProviderAvailability EnabledWithRequiredValue(bool enabled, string value)
    {
        if (!enabled)
        {
            return ProviderAvailability.Disabled;
        }

        return string.IsNullOrWhiteSpace(value)
            ? ProviderAvailability.NeedsSetup
            : ProviderAvailability.Available;
    }

    private static bool HasLoadedOptions(ArrConnectionOptions options)
    {
        return options.RootFolders.Count > 0 || options.QualityProfiles.Count > 0;
    }

    private RenderFragment CacheSummaryRow(string label, CacheBucketSummary summary) => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "cache-inspector-row");
        builder.OpenElement(2, "strong");
        builder.AddContent(3, label);
        builder.CloseElement();
        builder.OpenElement(4, "span");
        builder.AddContent(5, summary.Exists
            ? $"{summary.FileCount.ToString("N0", CultureInfo.InvariantCulture)} files · {FormatBytes(summary.TotalBytes)}"
            : "folder missing");
        builder.CloseElement();
        builder.OpenElement(6, "small");
        builder.AddContent(7, summary.LastWriteUtc is { } lastWrite
            ? $"Updated {FormatLocalTimestamp(lastWrite)}"
            : summary.Directory);
        builder.CloseElement();
        builder.CloseElement();
    };

    private async Task RefreshLocalStatusAsync()
    {
        try
        {
            _cacheSummary = CacheInspector.GetSummary();
            _jobEvents = await BackgroundJobTimeline.GetRecentAsync(_disposeCancellation.Token);
            _sourceHealth = await SourceHealthService.GetOverviewAsync(_disposeCancellation.Token);
            _systemStatus = ServiceProvider.GetService<SystemStatusService>() is { } systemStatusService
                ? await systemStatusService.GetAsync(_disposeCancellation.Token)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowToast(ToastKind.Error, "Status failed", ex.Message);
        }
    }

    private async Task BackfillScoresAsync()
    {
        _isBackfillingScores = true;
        try
        {
            var result = await DataMaintenanceService.BackfillRecentScoresAsync(_disposeCancellation.Token);
            _maintenanceResult = $"Score backfill scanned {result.ScannedCount.ToString("N0", CultureInfo.InvariantCulture)} cached cards and updated {result.ChangedCount.ToString("N0", CultureInfo.InvariantCulture)}.";
            ShowToast(ToastKind.Success, "Score backfill complete", _maintenanceResult);
            await RefreshLocalStatusAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowToast(ToastKind.Error, "Score backfill failed", ex.Message);
        }
        finally
        {
            _isBackfillingScores = false;
        }
    }

    private async Task RepairExternalIdsAsync()
    {
        _isRepairingExternalIds = true;
        try
        {
            var result = await DataMaintenanceService.RepairRecentExternalIdsAsync(_disposeCancellation.Token);
            _maintenanceResult = $"External ID repair scanned {result.ScannedCount.ToString("N0", CultureInfo.InvariantCulture)} cached cards and updated {result.ChangedCount.ToString("N0", CultureInfo.InvariantCulture)}.";
            ShowToast(ToastKind.Success, "External ID repair complete", _maintenanceResult);
            await RefreshLocalStatusAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowToast(ToastKind.Error, "External ID repair failed", ex.Message);
        }
        finally
        {
            _isRepairingExternalIds = false;
        }
    }

    private async Task ExportBackupAsync()
    {
        try
        {
            _backupJson = await SettingsBackupService.ExportAsync(_includeBackupSecrets, _disposeCancellation.Token);
            var message = _includeBackupSecrets
                ? "Settings backup JSON is ready with API secrets included."
                : "Settings backup JSON is ready with API secrets redacted.";
            ShowToast(ToastKind.Success, "Backup exported", message);
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "Backup failed", ex.Message);
        }
    }

    private async Task ImportBackupAsync()
    {
        try
        {
            var importNotices = await SettingsBackupService.ImportAsync(_backupJson, _disposeCancellation.Token);
            _secretDrafts.Clear();
            _settings = await SettingsStore.GetAsync(_disposeCancellation.Token);
            RememberPersistedArrUrls();
            HydrateDerivedSettingsFields();
            NormalizeSettings();
            HydrateDerivedSettingsFields();
            CreateEditContext();
            _isDirty = false;
            await RefreshLocalStatusAsync();
            ShowToast(ToastKind.Success, "Backup imported", "Settings were restored from the backup JSON.");
            foreach (var notice in importNotices)
            {
                AddKeyNotice(notice);
            }
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "Import failed", ex.Message);
        }
    }

    private static string JobStatusClass(BackgroundJobStatus status)
    {
        return status switch
        {
            BackgroundJobStatus.Succeeded => "job-status succeeded",
            BackgroundJobStatus.Failed => "job-status failed",
            BackgroundJobStatus.Skipped => "job-status skipped",
            _ => "job-status started"
        };
    }

    private IEnumerable<BackgroundJobEvent> VisibleJobEvents()
    {
        return _jobEvents
            .OrderBy(entry => entry.Status == BackgroundJobStatus.Failed ? 0 : 1)
            .ThenByDescending(entry => entry.OccurredUtc)
            .ThenBy(entry => entry.JobName, StringComparer.Ordinal)
            .Take(6);
    }

    private static string FormatLocalTimestamp(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value.ToString("0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    protected override async Task OnInitializedAsync()
    {
        var showTmdbRequiredNotice = HasTmdbRequiredReason();
        _tmdbReturnUrl = showTmdbRequiredNotice ? TmdbReturnUrl() : null;
        try
        {
            _settings = await SettingsStore.GetAsync();
            RememberPersistedArrUrls();
            _showTmdbRequiredNotice = showTmdbRequiredNotice && HasMissingTmdbToken();
            CreateEditContext();
            EnsureSimklActivityCheckInterval();
            HydrateDerivedSettingsFields();

            var optionLoadTasks = new List<Task>();
            if (CanLoadSonarrOptions())
            {
                optionLoadTasks.Add(LoadSonarrOptionsCoreAsync(showToast: false, timeout: TimeSpan.FromSeconds(3)));
            }

            if (CanLoadRadarrOptions())
            {
                optionLoadTasks.Add(LoadRadarrOptionsCoreAsync(showToast: false, timeout: TimeSpan.FromSeconds(3)));
            }

            if (optionLoadTasks.Count > 0)
            {
                await Task.WhenAll(optionLoadTasks);
            }

            await RefreshLocalStatusAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _loadError = "Settings could not be loaded.";
            _showTmdbRequiredNotice = false;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool HasMissingTmdbToken()
    {
        return string.IsNullOrWhiteSpace(_settings.Sources.Tmdb.BearerToken);
    }

    private bool HasTmdbRequiredReason()
    {
        try
        {
            var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
            return query.TryGetValue("reason", out var reason)
                && string.Equals(reason.ToString(), "tmdb", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private string? TmdbReturnUrl()
    {
        try
        {
            var query = QueryHelpers.ParseQuery(Navigation.ToAbsoluteUri(Navigation.Uri).Query);
            if (!query.TryGetValue("returnUrl", out var returnUrl))
            {
                return null;
            }

            var value = returnUrl.ToString();
            return LocalReturnUrl.IsSafe(value) ? value : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        await LoadViewSyncOverviewAsync();
    }

    private async Task LoadViewSyncOverviewAsync()
    {
        try
        {
            _viewSyncDeviceId = await JS.InvokeAsync<string?>("premiereViewSync.getOrCreateDeviceId")
                ?? Guid.NewGuid().ToString("N");
            _viewSyncOverview = await ViewSyncService.GetOverviewAsync(_viewSyncDeviceId, _disposeCancellation.Token);
            ApplyViewSyncOverview(_viewSyncOverview);
            _viewSyncLoaded = true;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException)
        {
            _viewSyncLoaded = true;
            _viewSyncEnabled = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "View sync overview load failed.");
            _viewSyncLoaded = true;
            _viewSyncEnabled = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void ApplyViewSyncOverview(ViewSyncOverview overview)
    {
        _viewSyncOverview = overview;
        _viewSyncDeviceName = overview.Device.DisplayName;
        _viewSyncEnabled = overview.Device.SyncEnabled;
        _selectedViewSyncGroupId = overview.Device.GroupId;
        _viewSyncGroups = overview.Groups;
        _viewSyncGroupDevices = overview.GroupDevices;
        _viewSyncGroupStates = overview.GroupStates is { Count: > 0 }
            ? overview.GroupStates
            : overview.GroupState is null
                ? []
                : [overview.GroupState];
        _viewSyncGroupOverviews = overview.GroupOverviews is { Count: > 0 }
            ? overview.GroupOverviews
            : BuildFallbackViewSyncGroupOverviews(overview);
    }

    private IReadOnlyList<ViewSyncGroupOverview> BuildFallbackViewSyncGroupOverviews(ViewSyncOverview overview)
    {
        if (overview.Groups.Count == 0)
        {
            return [];
        }

        return overview.Groups
            .Select(group => new ViewSyncGroupOverview(
                group,
                IsSelectedViewSyncGroup(group) ? _viewSyncGroupDevices : [],
                IsSelectedViewSyncGroup(group) ? _viewSyncGroupStates : []))
            .ToArray();
    }

    private IEnumerable<ViewSyncRouteSummary> ViewSyncRouteSummaries(IReadOnlyList<ViewSyncGroupState> states)
    {
        yield return ViewSyncRouteSummaryFor("All", "all", states);
        yield return ViewSyncRouteSummaryFor("Movies", "movies", states);
        yield return ViewSyncRouteSummaryFor("Series", "series", states);
    }

    private ViewSyncRouteSummary ViewSyncRouteSummaryFor(
        string label,
        string routeKey,
        IReadOnlyList<ViewSyncGroupState> states)
    {
        return new ViewSyncRouteSummary(
            label,
            states.FirstOrDefault(state =>
                string.Equals(state.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record ViewSyncRouteSummary(string Label, ViewSyncGroupState? State);

    private bool IsCurrentViewSyncDevice(ViewSyncDevice device)
    {
        return string.Equals(device.DeviceId, _viewSyncDeviceId, StringComparison.Ordinal);
    }

    private bool IsSelectedViewSyncGroup(ViewSyncGroup group)
    {
        return string.Equals(group.GroupId, _selectedViewSyncGroupId, StringComparison.Ordinal);
    }

    private string ViewSyncGroupBlockClass(ViewSyncGroup group)
    {
        return IsSelectedViewSyncGroup(group)
            ? "view-sync-group-block selected"
            : "view-sync-group-block";
    }

    private void SelectViewSyncGroup(string groupId)
    {
        _selectedViewSyncGroupId = groupId;
        _viewSyncEnabled = true;
    }

    private async Task SaveViewSyncSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(_viewSyncDeviceId))
        {
            return;
        }

        _isSavingViewSync = true;
        try
        {
            var overview = await ViewSyncService.SaveDeviceAsync(
                _viewSyncDeviceId,
                _viewSyncDeviceName,
                _viewSyncEnabled || !string.IsNullOrWhiteSpace(_selectedViewSyncGroupId),
                _selectedViewSyncGroupId,
                _disposeCancellation.Token);
            ApplyViewSyncOverview(overview);
            ShowToast(ToastKind.Success, "View sync saved", "This browser's sync settings were saved.");
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "View sync failed", ex.Message);
        }
        finally
        {
            _isSavingViewSync = false;
        }
    }

    private async Task CreateViewSyncGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(_viewSyncDeviceId))
        {
            return;
        }

        _isCreatingViewSyncGroup = true;
        try
        {
            var group = await ViewSyncService.CreateGroupAsync(_newViewSyncGroupName, _disposeCancellation.Token);
            _selectedViewSyncGroupId = group.GroupId;
            _newViewSyncGroupName = "";
            var overview = await ViewSyncService.SaveDeviceAsync(
                _viewSyncDeviceId,
                _viewSyncDeviceName,
                syncEnabled: true,
                _selectedViewSyncGroupId,
                _disposeCancellation.Token);
            ApplyViewSyncOverview(overview);
            ShowToast(ToastKind.Success, "View sync group created", $"Created {group.Name}.");
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "View sync failed", ex.Message);
        }
        finally
        {
            _isCreatingViewSyncGroup = false;
        }
    }

    private async Task UngroupViewSyncDeviceAsync()
    {
        if (string.IsNullOrWhiteSpace(_viewSyncDeviceId))
        {
            return;
        }

        _isUngroupingViewSync = true;
        try
        {
            var overview = await ViewSyncService.UngroupDeviceAsync(_viewSyncDeviceId, _disposeCancellation.Token);
            ApplyViewSyncOverview(overview);
            ShowToast(ToastKind.Success, "View sync disabled", "This browser was removed from its sync group.");
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "View sync failed", ex.Message);
        }
        finally
        {
            _isUngroupingViewSync = false;
        }
    }

    private static string FormatViewSyncLastSeen(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
    }

    private async Task ConnectSimklAsync()
    {
        _isRequestingSimklPin = true;
        try
        {
            StopSimklAuthorizationPolling();
            foreach (var notice in await PersistSettingsAsync())
            {
                AddKeyNotice(notice);
            }

            var result = await SimklClient.RequestPinCodeAsync(_disposeCancellation.Token);
            if (!result.Success)
            {
                _simklPinCode = null;
                _simklAuthorizationStatus = null;
                ShowToast(ToastKind.Error, "SIMKL PIN failed", result.Error ?? "SIMKL did not return a PIN.");
                return;
            }

            _simklPinCode = result;
            _simklAuthorizationStatus = "Waiting for authorization";
            StartSimklAuthorizationPolling(result);
            ShowToast(ToastKind.Info, "SIMKL authorization started", "Open SIMKL, enter the displayed code, and leave this page open.");
        }
        catch (Exception ex)
        {
            _simklPinCode = null;
            _simklAuthorizationStatus = null;
            ShowToast(ToastKind.Error, "SIMKL PIN failed", ex.Message);
        }
        finally
        {
            _isRequestingSimklPin = false;
        }
    }

    private async Task SaveSimklTokenAfterAuthorizationAsync()
    {
        if (_simklPinCode?.UserCode is not { Length: > 0 } userCode)
        {
            ShowToast(ToastKind.Error, "SIMKL authorization failed", "Connect SIMKL first.");
            return;
        }

        _isCheckingSimklPin = true;
        try
        {
            await CheckSimklAuthorizationCoreAsync(userCode, showToast: true, _disposeCancellation.Token);
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "SIMKL authorization failed", ex.Message);
        }
        finally
        {
            _isCheckingSimklPin = false;
        }
    }

    private void StartSimklAuthorizationPolling(SimklPinCodeResult pinCode)
    {
        StopSimklAuthorizationPolling();
        if (string.IsNullOrWhiteSpace(pinCode.UserCode))
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellation.Token);
        _simklPinPollingCancellation = cancellation;
        _ = PollSimklAuthorizationAsync(pinCode, cancellation.Token);
    }

    private async Task PollSimklAuthorizationAsync(SimklPinCodeResult pinCode, CancellationToken cancellationToken)
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, pinCode.ExpiresInSeconds));
        var delay = TimeSpan.FromSeconds(Math.Clamp(pinCode.IntervalSeconds, 1, 60));
        var userCode = pinCode.UserCode ?? "";

        try
        {
            while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow < expiresAtUtc)
            {
                await Task.Delay(delay, cancellationToken);
                if (_simklPinCode?.UserCode != userCode)
                {
                    return;
                }

                try
                {
                    var completed = await CheckSimklAuthorizationCoreAsync(userCode, showToast: false, cancellationToken);
                    if (completed)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _simklAuthorizationStatus = $"Waiting for authorization. Last check failed: {ex.Message}";
                    await InvokeAsync(StateHasChanged);
                }
            }

            if (!cancellationToken.IsCancellationRequested && _simklPinCode?.UserCode == userCode)
            {
                _simklAuthorizationStatus = "Authorization code expired";
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task<bool> CheckSimklAuthorizationCoreAsync(string userCode, bool showToast, CancellationToken cancellationToken)
    {
        var result = await SimklClient.CheckPinCodeAsync(userCode, cancellationToken);
        switch (result.Status)
        {
            case SimklPinStatus.Authorized when !string.IsNullOrWhiteSpace(result.AccessToken):
                // Persist only the token SIMKL just returned; other typed secrets, pending Clears and edits stay unsaved.
                var token = result.AccessToken.Trim();
                _secretDrafts.Remove(SecretField.SimklAccessToken);
                _settings.Sources.Simkl.AccessToken = token;
                var persisted = await SettingsStore.GetAsync(cancellationToken);
                await SettingsStore.SaveAsync(
                    persisted with
                    {
                        Sources = persisted.Sources with
                        {
                            Simkl = persisted.Sources.Simkl with { AccessToken = token }
                        }
                    },
                    cancellationToken);
                _simklPinCode = null;
                _simklAuthorizationStatus = null;
                StopSimklAuthorizationPolling();
                ShowToast(ToastKind.Success, "SIMKL connected", "Access token was saved locally.");
                await InvokeAsync(StateHasChanged);
                return true;
            case SimklPinStatus.Pending:
                _simklAuthorizationStatus = "Waiting for authorization";
                if (showToast)
                {
                    ShowToast(ToastKind.Info, "SIMKL authorization pending", result.Message ?? "Enter the displayed code on SIMKL, then check again.");
                }

                await InvokeAsync(StateHasChanged);
                return false;
            case SimklPinStatus.SlowDown:
                _simklAuthorizationStatus = "Waiting for authorization";
                if (showToast)
                {
                    ShowToast(ToastKind.Info, "SIMKL asked to slow down", result.Message ?? "Wait a few seconds before checking again.");
                }

                await InvokeAsync(StateHasChanged);
                return false;
            default:
                _simklAuthorizationStatus = result.Message ?? "Authorization failed";
                _simklPinCode = null;
                StopSimklAuthorizationPolling();
                if (showToast)
                {
                    ShowToast(ToastKind.Error, "SIMKL authorization failed", result.Message ?? "SIMKL did not authorize this PIN.");
                }

                await InvokeAsync(StateHasChanged);
                return true;
        }
    }

    private void StopSimklAuthorizationPolling()
    {
        var cancellation = _simklPinPollingCancellation;
        _simklPinPollingCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task SaveAsync()
    {
        _isSaving = true;
        try
        {
            var notices = await PersistSettingsAsync();
            _isDirty = false;
            foreach (var notice in notices)
            {
                AddKeyNotice(notice);
            }

            if (!HasMissingTmdbToken())
            {
                _showTmdbRequiredNotice = false;
            }

            ShowToast(ToastKind.Success, "Settings saved", "Integration and source API parameters were saved to the local database.");
            if (!HasMissingTmdbToken() && !string.IsNullOrWhiteSpace(_tmdbReturnUrl))
            {
                Navigation.NavigateTo(_tmdbReturnUrl, replace: true);
            }
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "Settings not saved", ex.Message);
        }
        finally
        {
            _isSaving = false;
        }
    }

    private async Task LoadSonarrOptionsAsync()
    {
        await LoadSonarrOptionsCoreAsync(showToast: true);
    }

    private async Task LoadSonarrOptionsCoreAsync(bool showToast, TimeSpan? timeout = null)
    {
        if (ArrUrlChanged(SecretField.SonarrApiKey) && !HasSecretDraft(SecretField.SonarrApiKey))
        {
            if (showToast)
            {
                ShowToast(ToastKind.Error, "Sonarr API key required", "The Sonarr URL changed, so the saved API key is not sent to it. Type the API key to test the connection.");
            }

            return;
        }

        _isLoadingSonarr = true;
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        try
        {
            _sonarrOptions = await ArrService.GetSonarrOptionsAsync(
                _settings.Sonarr with { ApiKey = EffectiveSecret(SecretField.SonarrApiKey) },
                timeoutCts?.Token ?? default);
            _settings.Sonarr.RootFolderPath = FirstRootIfEmpty(_settings.Sonarr.RootFolderPath, _sonarrOptions);
            _settings.Sonarr.QualityProfileId ??= _sonarrOptions.QualityProfiles.FirstOrDefault()?.Id;
            if (showToast)
            {
                ShowToast(ToastKind.Success, "Sonarr connected", "Root folders and quality profiles were loaded.");
            }
        }
        catch (Exception ex)
        {
            if (showToast)
            {
                ShowToast(ToastKind.Error, "Sonarr connection failed", ex.Message);
            }
        }
        finally
        {
            _isLoadingSonarr = false;
        }
    }

    private async Task LoadRadarrOptionsAsync()
    {
        await LoadRadarrOptionsCoreAsync(showToast: true);
    }

    private async Task LoadRadarrOptionsCoreAsync(bool showToast, TimeSpan? timeout = null)
    {
        if (ArrUrlChanged(SecretField.RadarrApiKey) && !HasSecretDraft(SecretField.RadarrApiKey))
        {
            if (showToast)
            {
                ShowToast(ToastKind.Error, "Radarr API key required", "The Radarr URL changed, so the saved API key is not sent to it. Type the API key to test the connection.");
            }

            return;
        }

        _isLoadingRadarr = true;
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        try
        {
            _radarrOptions = await ArrService.GetRadarrOptionsAsync(
                _settings.Radarr with { ApiKey = EffectiveSecret(SecretField.RadarrApiKey) },
                timeoutCts?.Token ?? default);
            _settings.Radarr.RootFolderPath = FirstRootIfEmpty(_settings.Radarr.RootFolderPath, _radarrOptions);
            _settings.Radarr.QualityProfileId ??= _radarrOptions.QualityProfiles.FirstOrDefault()?.Id;
            if (showToast)
            {
                ShowToast(ToastKind.Success, "Radarr connected", "Root folders and quality profiles were loaded.");
            }
        }
        catch (Exception ex)
        {
            if (showToast)
            {
                ShowToast(ToastKind.Error, "Radarr connection failed", ex.Message);
            }
        }
        finally
        {
            _isLoadingRadarr = false;
        }
    }

    private bool CanLoadSonarrOptions()
    {
        return _settings.Sonarr.Enabled
            && !string.IsNullOrWhiteSpace(_settings.Sonarr.BaseUrl)
            && !string.IsNullOrWhiteSpace(EffectiveSecret(SecretField.SonarrApiKey));
    }

    private bool CanLoadRadarrOptions()
    {
        return _settings.Radarr.Enabled
            && !string.IsNullOrWhiteSpace(_settings.Radarr.BaseUrl)
            && !string.IsNullOrWhiteSpace(EffectiveSecret(SecretField.RadarrApiKey));
    }

    private void NormalizeSettings()
    {
        _settings.Sonarr.BaseUrl = NormalizeUrl(_settings.Sonarr.BaseUrl);
        _settings.Sonarr.ApiKey = _settings.Sonarr.ApiKey.Trim();
        _settings.Sonarr.RootFolderPath = _settings.Sonarr.RootFolderPath.Trim();
        _settings.Sonarr.SeriesType = string.IsNullOrWhiteSpace(_settings.Sonarr.SeriesType) ? "standard" : _settings.Sonarr.SeriesType.Trim();
        _settings.Sonarr.Monitor = string.IsNullOrWhiteSpace(_settings.Sonarr.Monitor) ? "all" : _settings.Sonarr.Monitor.Trim();
        _settings.Sonarr.TagOnAdd = _settings.Sonarr.TagOnAdd.Trim();

        _settings.Radarr.BaseUrl = NormalizeUrl(_settings.Radarr.BaseUrl);
        _settings.Radarr.ApiKey = _settings.Radarr.ApiKey.Trim();
        _settings.Radarr.RootFolderPath = _settings.Radarr.RootFolderPath.Trim();
        _settings.Radarr.MinimumAvailability = string.IsNullOrWhiteSpace(_settings.Radarr.MinimumAvailability)
            ? "released"
            : _settings.Radarr.MinimumAvailability.Trim();
        _settings.Radarr.TagOnAdd = _settings.Radarr.TagOnAdd.Trim();

        _settings.Sources.Tmdb.BearerToken = _settings.Sources.Tmdb.BearerToken.Trim();
        _settings.Sources.Tvmaze.ScheduleCountries = ParseCountryCodes(_tvmazeScheduleCountries);
        _settings.Sources.Trakt.ClientId = _settings.Sources.Trakt.ClientId.Trim();
        _settings.Sources.Watchmode.ApiKey = _settings.Sources.Watchmode.ApiKey.Trim();
        _settings.Sources.Watchmode.Regions = ParseCountryCodes(_watchmodeRegions);
        _settings.Sources.Watchmode.EnableReleaseDiscovery = false;
        _settings.Sources.Simkl.ClientId = _settings.Sources.Simkl.ClientId.Trim();
        _settings.Sources.Simkl.ClientSecret = _settings.Sources.Simkl.ClientSecret.Trim();
        _settings.Sources.Simkl.AccessToken = _settings.Sources.Simkl.AccessToken.Trim();
        EnsureSimklActivityCheckInterval();

        _settings.Sources.Omdb.ApiKey = _settings.Sources.Omdb.ApiKey.Trim();
        _settings.Sources.Fanart.ApiKey = _settings.Sources.Fanart.ApiKey.Trim();
        _settings.Sources.TheTvdb.ApiKey = _settings.Sources.TheTvdb.ApiKey.Trim();
    }

    private void HydrateDerivedSettingsFields()
    {
        _tvmazeScheduleCountries = string.Join(", ", _settings.Sources.Tvmaze.ScheduleCountries);
        _watchmodeRegions = string.Join(", ", _settings.Sources.Watchmode.Regions);
    }

    private void EnsureSimklActivityCheckInterval()
    {
        if (_settings.Sources.Simkl.MinimumActivityCheckMinutes is null or <= 0)
        {
            _settings.Sources.Simkl.MinimumActivityCheckMinutes = 30;
        }
    }

    private void CreateEditContext()
    {
        if (_editContext is not null)
        {
            _editContext.OnFieldChanged -= MarkSettingsDirty;
        }

        _editContext = new EditContext(_settings);
        _editContext.OnFieldChanged += MarkSettingsDirty;
    }

    private void MarkSettingsDirty(object? sender, FieldChangedEventArgs args)
    {
        _isDirty = true;
    }

    private static string[] ParseCountryCodes(string value)
    {
        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FirstRootIfEmpty(string currentValue, ArrConnectionOptions options)
    {
        return string.IsNullOrWhiteSpace(currentValue)
            ? options.RootFolders.FirstOrDefault()?.Path ?? ""
            : currentValue;
    }

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed;
    }

    private void ShowToast(ToastKind kind, string title, string message)
    {
        var toast = new ToastMessage(Guid.NewGuid(), kind, title, message);
        _toasts.Add(toast);
        _ = RemoveToastAfterDelayAsync(toast.Id, _disposeCancellation.Token);
    }

    private async Task RemoveToastAfterDelayAsync(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _toasts.RemoveAll(toast => toast.Id == id);
            await InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    StateHasChanged();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        _disposeCancellation.Cancel();
        if (_editContext is not null)
        {
            _editContext.OnFieldChanged -= MarkSettingsDirty;
        }

        StopSimklAuthorizationPolling();
        _disposeCancellation.Dispose();
    }

    private sealed record ProviderAvailability(string Text, string CssClass)
    {
        public static ProviderAvailability Online { get; } = new("Online", "online");
        public static ProviderAvailability Available { get; } = new("Configured", "configured");
        public static ProviderAvailability NeedsSetup { get; } = new("Needs setup", "needs-setup");
        public static ProviderAvailability Disabled { get; } = new("Disabled", "disabled");
    }
}
