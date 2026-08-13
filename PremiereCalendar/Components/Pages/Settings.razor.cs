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
    private ReleaseUpdateResult? _releaseResult;
    private bool _isCheckingRelease;
    private ApplicationUpdateStatus? _applicationUpdateStatus;
    private ApplicationUpdateStartResult? _applicationUpdateResult;
    private bool _isStartingApplicationUpdate;
    private bool _isBackfillingScores;
    private bool _isRepairingExternalIds;
    private string? _maintenanceResult;
    private string _backupJson = "";
    private bool _includeBackupSecrets;

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
            _applicationUpdateStatus = ApplicationUpdateService.GetStatus();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowToast(ToastKind.Error, "Status failed", ex.Message);
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        _isCheckingRelease = true;
        try
        {
            _releaseResult = await ReleaseUpdateService.CheckLatestAsync(_disposeCancellation.Token);
            var message = !_releaseResult.HasPublishedRelease
                ? "No published GitHub releases were found."
                : _releaseResult.IsUpdateAvailable
                ? $"Version {_releaseResult.LatestVersion} is available."
                : "This install is up to date.";
            ShowToast(ToastKind.Info, "Update check complete", message);
        }
        catch (Exception ex)
        {
            ShowToast(ToastKind.Error, "Update check failed", ex.Message);
        }
        finally
        {
            _isCheckingRelease = false;
        }
    }

    private async Task StartApplicationUpdateAsync()
    {
        _isStartingApplicationUpdate = true;
        try
        {
            _applicationUpdateResult = await ApplicationUpdateService.StartUpdateAsync(_disposeCancellation.Token);
            _applicationUpdateStatus = ApplicationUpdateService.GetStatus();
            if (_applicationUpdateResult.Started)
            {
                ShowToast(
                    ToastKind.Info,
                    "Application update started",
                    "The signed GitHub release updater started. The app may reconnect after verified activation.");
            }
            else
            {
                ShowToast(ToastKind.Error, "Application update not started", _applicationUpdateResult.Message);
            }
        }
        catch (Exception ex)
        {
            _applicationUpdateResult = new ApplicationUpdateStartResult(false, ex.Message, null);
            ShowToast(ToastKind.Error, "Application update failed", ex.Message);
        }
        finally
        {
            _isStartingApplicationUpdate = false;
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
            await SettingsBackupService.ImportAsync(_backupJson, _disposeCancellation.Token);
            _settings = await SettingsStore.GetAsync(_disposeCancellation.Token);
            HydrateDerivedSettingsFields();
            NormalizeSettings();
            HydrateDerivedSettingsFields();
            CreateEditContext();
            _isDirty = false;
            await RefreshLocalStatusAsync();
            ShowToast(ToastKind.Success, "Backup imported", "Settings were restored from the backup JSON.");
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

    private string ApplicationUpdateResultClass => _applicationUpdateResult?.Started == true
        ? "settings-help update-result started"
        : "settings-help update-result failed";

    private string ApplicationUpdateStatusText()
    {
        if (_applicationUpdateStatus is null)
        {
            return "Update status is not loaded yet.";
        }

        var prefix = _applicationUpdateStatus.IsConfigured
            ? "Ready"
            : "Not ready";
        var logText = string.IsNullOrWhiteSpace(_applicationUpdateStatus.LatestLogPath)
            ? ""
            : $" Latest log: {Path.GetFileName(_applicationUpdateStatus.LatestLogPath)}.";
        return $"{prefix}: signed releases from {_applicationUpdateStatus.Repository}. {_applicationUpdateStatus.Message}{logText}";
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
            NormalizeSettings();
            await SettingsStore.SaveAsync(_settings);

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
                _settings.Sources.Simkl.AccessToken = result.AccessToken;
                NormalizeSettings();
                await SettingsStore.SaveAsync(_settings, cancellationToken);
                _isDirty = false;
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
            NormalizeSettings();
            await SettingsStore.SaveAsync(_settings);
            _isDirty = false;
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
        _isLoadingSonarr = true;
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        try
        {
            _sonarrOptions = await ArrService.GetSonarrOptionsAsync(_settings.Sonarr, timeoutCts?.Token ?? default);
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
        _isLoadingRadarr = true;
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        try
        {
            _radarrOptions = await ArrService.GetRadarrOptionsAsync(_settings.Radarr, timeoutCts?.Token ?? default);
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
            && !string.IsNullOrWhiteSpace(_settings.Sonarr.ApiKey);
    }

    private bool CanLoadRadarrOptions()
    {
        return _settings.Radarr.Enabled
            && !string.IsNullOrWhiteSpace(_settings.Radarr.BaseUrl)
            && !string.IsNullOrWhiteSpace(_settings.Radarr.ApiKey);
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
