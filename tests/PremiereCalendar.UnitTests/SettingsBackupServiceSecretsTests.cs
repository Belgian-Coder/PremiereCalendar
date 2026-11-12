using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SettingsBackupServiceSecretsTests
{
    private static readonly string[] AllSecrets =
    [
        "sonarr-key", "radarr-key", "tmdb-token", "trakt-id", "omdb-key", "fanart-key",
        "tvdb-key", "watchmode-key", "simkl-id", "simkl-secret", "simkl-token"
    ];

    private static IntegrationSettings SettingsWithSecrets(string suffix = "", bool enabled = true)
    {
        string S(string value) => value + suffix;
        return new IntegrationSettings
        {
            Sonarr = new SonarrIntegrationSettings { Enabled = enabled, BaseUrl = "http://sonarr" + suffix, ApiKey = S("sonarr-key") },
            Radarr = new RadarrIntegrationSettings { Enabled = enabled, BaseUrl = "http://radarr" + suffix, ApiKey = S("radarr-key") },
            Sources = new SourceIntegrationSettings
            {
                Tmdb = new TmdbSourceSettings { BearerToken = S("tmdb-token") },
                Trakt = new TraktSourceSettings { ClientId = S("trakt-id") },
                Omdb = new OmdbSourceSettings { Enabled = enabled, ApiKey = S("omdb-key") },
                Fanart = new FanartSourceSettings { Enabled = enabled, ApiKey = S("fanart-key") },
                TheTvdb = new TheTvdbSourceSettings { Enabled = enabled, ApiKey = S("tvdb-key") },
                Watchmode = new WatchmodeSourceSettings { ApiKey = S("watchmode-key") },
                Simkl = new SimklSourceSettings
                {
                    ClientId = S("simkl-id"),
                    ClientSecret = S("simkl-secret"),
                    AccessToken = S("simkl-token")
                }
            }
        };
    }

    private static void AssertSecrets(IntegrationSettings settings, string suffix)
    {
        Assert.Equal("sonarr-key" + suffix, settings.Sonarr.ApiKey);
        Assert.Equal("radarr-key" + suffix, settings.Radarr.ApiKey);
        Assert.Equal("tmdb-token" + suffix, settings.Sources.Tmdb.BearerToken);
        Assert.Equal("trakt-id" + suffix, settings.Sources.Trakt.ClientId);
        Assert.Equal("omdb-key" + suffix, settings.Sources.Omdb.ApiKey);
        Assert.Equal("fanart-key" + suffix, settings.Sources.Fanart.ApiKey);
        Assert.Equal("tvdb-key" + suffix, settings.Sources.TheTvdb.ApiKey);
        Assert.Equal("watchmode-key" + suffix, settings.Sources.Watchmode.ApiKey);
        Assert.Equal("simkl-id" + suffix, settings.Sources.Simkl.ClientId);
        Assert.Equal("simkl-secret" + suffix, settings.Sources.Simkl.ClientSecret);
        Assert.Equal("simkl-token" + suffix, settings.Sources.Simkl.AccessToken);
    }

    [Fact]
    public async Task RedactedExportRoundTripKeepsStoredSecretsAndRestoresOtherSettings()
    {
        var store = new FakeSettingsStore(SettingsWithSecrets());
        var service = new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System);
        var json = await service.ExportAsync(includeSecrets: false, CancellationToken.None);
        foreach (var secret in AllSecrets)
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        // Change non-secret settings after the export so the import has something to restore.
        store.Current.Sonarr.Enabled = false;
        store.Current.Sonarr.ApiKey = "rotated-sonarr-key";

        var notices = await service.ImportAsync(json, CancellationToken.None);

        Assert.Empty(notices);
        Assert.Equal(1, store.SaveCount);
        Assert.True(store.Current.Sonarr.Enabled);
        Assert.Equal("rotated-sonarr-key", store.Current.Sonarr.ApiKey);
        Assert.Equal("radarr-key", store.Current.Radarr.ApiKey);
        Assert.Equal("tmdb-token", store.Current.Sources.Tmdb.BearerToken);
        Assert.Equal("simkl-token", store.Current.Sources.Simkl.AccessToken);
        Assert.Equal("watchmode-key", store.Current.Sources.Watchmode.ApiKey);
    }

    [Fact]
    public async Task RedactedEnvelopeNeverReplacesSecretsEvenIfItContainsValues()
    {
        var store = new FakeSettingsStore(SettingsWithSecrets("-stored"));
        var service = new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System);
        var other = SettingsWithSecrets("-other");
        other.Sonarr.BaseUrl = "http://sonarr-stored";
        other.Radarr.BaseUrl = "http://radarr-stored";
        var otherJson = await new SettingsBackupService(
                new FakeSettingsStore(other), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: true, CancellationToken.None);
        var json = otherJson.Replace("\"includeSecrets\": true", "\"includeSecrets\": false", StringComparison.Ordinal);
        Assert.Contains("\"includeSecrets\": false", json, StringComparison.Ordinal);

        await service.ImportAsync(json, CancellationToken.None);

        AssertSecrets(store.Current, "-stored");
    }

    [Theory]
    [InlineData(false, "http://evil.example/")]
    [InlineData(true, "http://evil.example/")]
    public async Task ImportWithChangedArrUrlAndNoNewKeyRemovesTheStoredKey(bool includeSecrets, string newUrl)
    {
        var backupSettings = SettingsWithSecrets();
        backupSettings.Sonarr.BaseUrl = newUrl;
        backupSettings.Radarr.BaseUrl = newUrl;
        if (includeSecrets)
        {
            backupSettings.Sonarr.ApiKey = "";
            backupSettings.Radarr.ApiKey = " ";
        }

        var json = await new SettingsBackupService(
                new FakeSettingsStore(backupSettings), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets, CancellationToken.None);
        var store = new FakeSettingsStore(SettingsWithSecrets());

        var notices = await new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
            .ImportAsync(json, CancellationToken.None);

        Assert.Equal(newUrl, store.Current.Sonarr.BaseUrl);
        Assert.Equal("", store.Current.Sonarr.ApiKey);
        Assert.Equal("", store.Current.Radarr.ApiKey);
        Assert.Equal(2, notices.Count);
        Assert.Contains("Sonarr", notices[0]);
        Assert.Equal("tmdb-token", store.Current.Sources.Tmdb.BearerToken);
    }

    [Fact]
    public async Task ImportWithChangedArrUrlAndANewKeyUsesTheNewKey()
    {
        var backupSettings = SettingsWithSecrets("-new");
        backupSettings.Sonarr.BaseUrl = "http://other-host/";
        var json = await new SettingsBackupService(
                new FakeSettingsStore(backupSettings), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: true, CancellationToken.None);
        var store = new FakeSettingsStore(SettingsWithSecrets("-old"));

        var notices = await new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
            .ImportAsync(json, CancellationToken.None);

        Assert.Empty(notices);
        Assert.Equal("sonarr-key-new", store.Current.Sonarr.ApiKey);
    }

    [Fact]
    public async Task ImportWithSameArrUrlIgnoringCaseAndTrailingSlashKeepsTheStoredKey()
    {
        var backupSettings = SettingsWithSecrets();
        backupSettings.Sonarr.BaseUrl = "HTTP://SONARR/";
        var json = await new SettingsBackupService(
                new FakeSettingsStore(backupSettings), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: false, CancellationToken.None);
        var store = new FakeSettingsStore(SettingsWithSecrets());

        var notices = await new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
            .ImportAsync(json, CancellationToken.None);

        Assert.Empty(notices);
        Assert.Equal("sonarr-key", store.Current.Sonarr.ApiKey);
    }

    [Theory]
    [InlineData("tmdb")]
    [InlineData("tvmaze")]
    [InlineData("trakt")]
    [InlineData("omdb")]
    [InlineData("fanart")]
    [InlineData("theTvdb")]
    [InlineData("wikimedia")]
    [InlineData("watchmode")]
    [InlineData("simkl")]
    public async Task ImportRejectsAnEnvelopeMissingOrNullingAnySourceSection(string section)
    {
        var json = await new SettingsBackupService(
                new FakeSettingsStore(SettingsWithSecrets()), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: true, CancellationToken.None);
        foreach (var variant in new[] { RemoveSection(json, section, null), RemoveSection(json, section, "null") })
        {
            var store = new FakeSettingsStore(SettingsWithSecrets("-keep"));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
                    .ImportAsync(variant, CancellationToken.None));

            Assert.Contains(section, error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, store.SaveCount);
        }
    }

    [Theory]
    [InlineData("sonarr", "apiKey")]
    [InlineData("sonarr", "baseUrl")]
    [InlineData("radarr", "rootFolderPath")]
    public async Task ImportRejectsNullStrings(string section, string property)
    {
        var json = await new SettingsBackupService(
                new FakeSettingsStore(SettingsWithSecrets()), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: true, CancellationToken.None);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["settings"]![section]![property] = null;
        var store = new FakeSettingsStore(SettingsWithSecrets());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
                .ImportAsync(node.ToJsonString(), CancellationToken.None));

        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ImportRejectsNullScheduleCountriesAndAcceptsNullOptionalNumbers()
    {
        var json = await new SettingsBackupService(
                new FakeSettingsStore(SettingsWithSecrets()), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: true, CancellationToken.None);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        node["settings"]!["sonarr"]!["qualityProfileId"] = null;
        var store = new FakeSettingsStore(SettingsWithSecrets());
        await new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
            .ImportAsync(node.ToJsonString(), CancellationToken.None);
        Assert.Equal(1, store.SaveCount);

        node["settings"]!["sources"]!["tvmaze"]!["scheduleCountries"] = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
                .ImportAsync(node.ToJsonString(), CancellationToken.None));
        Assert.Equal(1, store.SaveCount);
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("  ", "", true)]
    [InlineData("http://sonarr:8989", "http://sonarr:8989/", true)]
    [InlineData("HTTP://SONARR.Lan:8989/", "http://sonarr.lan:8989", true)]
    [InlineData("http://sonarr", "http://sonarr:80", true)]
    [InlineData("https://sonarr", "https://sonarr:443/", true)]
    [InlineData("http://sonarr/app", "http://sonarr/app/", true)]
    [InlineData("https://sonarr.mydomain.com", "https://sonarr.mydomaın.com", false)]
    [InlineData("http://sonarr", "https://sonarr", false)]
    [InlineData("http://sonarr:80", "http://sonarr:8080", false)]
    [InlineData("http://sonarr/App", "http://sonarr/app", false)]
    [InlineData("http://sonarr", "http://sonarr/app", false)]
    [InlineData("http://user@sonarr", "http://sonarr", false)]
    [InlineData("http://a:b@sonarr", "http://a:c@sonarr", false)]
    [InlineData("http://sonarr", "", false)]
    [InlineData("ftp://sonarr", "ftp://sonarr/x", false)]
    [InlineData("not a url", "other not a url", false)]
    public void SameBaseUrl_ComparesParsedEndpointParts(string left, string right, bool expected)
    {
        Assert.Equal(expected, SecretHostBinding.SameBaseUrl(left, right));
        Assert.Equal(expected, SecretHostBinding.SameBaseUrl(right, left));
    }

    [Fact]
    public async Task ImportWithLookAlikeUnicodeHostRemovesTheStoredKey()
    {
        var backupSettings = SettingsWithSecrets();
        backupSettings.Sonarr.BaseUrl = "http://sonarr.mydomaın.com";
        var stored = SettingsWithSecrets();
        stored.Sonarr.BaseUrl = "http://sonarr.mydomain.com";
        var json = await new SettingsBackupService(
                new FakeSettingsStore(backupSettings), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: false, CancellationToken.None);
        var store = new FakeSettingsStore(stored);

        var notices = await new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
            .ImportAsync(json, CancellationToken.None);

        Assert.Equal("", store.Current.Sonarr.ApiKey);
        Assert.Single(notices);
    }

    private static string RemoveSection(string json, string section, string? nullLiteral)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var sources = node["settings"]!["sources"]!.AsObject();
        if (nullLiteral is null)
        {
            sources.Remove(section);
        }
        else
        {
            sources[section] = null;
        }

        return node.ToJsonString();
    }

    [Fact]
    public async Task EnvelopeWithSecretsReplacesStoredSecrets()
    {
        var store = new FakeSettingsStore(SettingsWithSecrets("-old", enabled: false));
        var source = new SettingsBackupService(
            new FakeSettingsStore(SettingsWithSecrets("-new")), new FakeStateStore(), TimeProvider.System);
        var json = await source.ExportAsync(includeSecrets: true, CancellationToken.None);
        var target = new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System);

        await target.ImportAsync(json, CancellationToken.None);

        AssertSecrets(store.Current, "-new");
        Assert.True(store.Current.Sonarr.Enabled);
    }

    [Fact]
    public async Task EnvelopeWithSecretsKeepsStoredSecretWhereTheBackupFieldIsBlank()
    {
        var backupSettings = SettingsWithSecrets("-new");
        backupSettings.Sources.Omdb.ApiKey = "";
        backupSettings.Sources.Simkl.AccessToken = "  ";
        var json = await new SettingsBackupService(
                new FakeSettingsStore(backupSettings), new FakeStateStore(), TimeProvider.System)
            .ExportAsync(includeSecrets: true, CancellationToken.None);
        var store = new FakeSettingsStore(SettingsWithSecrets("-old"));

        await new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System)
            .ImportAsync(json, CancellationToken.None);

        Assert.Equal("omdb-key-old", store.Current.Sources.Omdb.ApiKey);
        Assert.Equal("simkl-token-old", store.Current.Sources.Simkl.AccessToken);
        Assert.Equal("sonarr-key-new", store.Current.Sonarr.ApiKey);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"exportedUtc\":\"2026-01-01T00:00:00Z\",\"includeSecrets\":false,\"settings\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"exportedUtc\":\"2026-01-01T00:00:00Z\",\"includeSecrets\":true,\"settings\":null}")]
    [InlineData("{\"schemaVersion\":1,\"exportedUtc\":\"2026-01-01T00:00:00Z\",\"includeSecrets\":true}")]
    [InlineData("{\"schemaVersion\":1,\"includeSecrets\":true,\"settings\":{\"sonarr\":{},\"radarr\":{}}}")]
    [InlineData("{\"schemaVersion\":1,\"includeSecrets\":true,\"settings\":{\"sonarr\":{},\"radarr\":{},\"sources\":{}}}")]
    [InlineData("{\"schemaVersion\":1,\"includeSecrets\":true,\"settings\":{\"sonarr\":{},\"radarr\":{},\"sources\":{\"tmdb\":{}}}}")]
    [InlineData("{\"schemaVersion\":1,\"includeSecrets\":true,\"settings\":{\"sonarr\":null,\"radarr\":{},\"sources\":{\"tmdb\":{}}}}")]
    [InlineData("{\"schemaVersion\":1,\"includeSecrets\":true,\"settings\":[]}")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task EmptyOrMalformedEnvelopeIsRejectedAndNothingIsSaved(string json)
    {
        var store = new FakeSettingsStore(SettingsWithSecrets());
        var state = new FakeStateStore();
        state.Values["Calendar.keep"] = "1";
        var service = new SettingsBackupService(store, state, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(json, CancellationToken.None));

        Assert.Equal(0, store.SaveCount);
        AssertSecrets(store.Current, "");
        Assert.Equal("1", state.Values["Calendar.keep"]);
    }

    [Fact]
    public async Task ExportedEnvelopeStillRoundTripsThroughValidation()
    {
        var store = new FakeSettingsStore(new IntegrationSettings());
        var service = new SettingsBackupService(store, new FakeStateStore(), TimeProvider.System);
        var json = await service.ExportAsync(includeSecrets: true, CancellationToken.None);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("settings").ValueKind);

        await service.ImportAsync(json, CancellationToken.None);

        Assert.Equal(1, store.SaveCount);
    }

    private sealed class FakeSettingsStore(IntegrationSettings initial) : IIntegrationSettingsStore
    {
        public IntegrationSettings Current { get; private set; } = initial;
        public int SaveCount { get; private set; }

        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStateStore : IAppStateStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            Values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, string> values = Values
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            return Task.FromResult(values);
        }
    }
}
