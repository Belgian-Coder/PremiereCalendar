using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TvmazeScheduleDiscoveryProviderTests
{
    [Fact]
    public async Task GetCandidatesAsync_QueriesConfiguredBroadcastWebAndGlobalWebSchedules()
    {
        var client = new FakeTvmazeClient
        {
            ScheduleItems =
            [
                new TvmazeScheduleEpisode
                {
                    Season = 1,
                    Number = 1,
                    Airdate = "2026-05-04",
                    Embedded = new TvmazeEmbedded
                    {
                        Show = new TvmazeShow
                        {
                            Name = "Mappable Premiere",
                            Language = "Dutch",
                            Externals = new TvmazeExternals { TheTvdb = 12345, Imdb = "tt12345" }
                        }
                    }
                },
                new TvmazeScheduleEpisode
                {
                    Season = 1,
                    Number = 2,
                    Airdate = "2026-05-04",
                    Embedded = new TvmazeEmbedded
                    {
                        Show = new TvmazeShow
                        {
                            Name = "Episode Two",
                            Externals = new TvmazeExternals { TheTvdb = 22222 }
                        }
                    }
                },
                new TvmazeScheduleEpisode
                {
                    Season = 1,
                    Number = 1,
                    Airdate = "2026-05-04",
                    Embedded = new TvmazeEmbedded
                    {
                        Show = new TvmazeShow { Name = "Unmappable Premiere" }
                    }
                }
            ]
        };
        var provider = CreateProvider(client);

        var candidates = await provider.GetCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 4),
            CancellationToken.None);

        Assert.Equal(3, candidates.Count);
        var premiere = Assert.Single(candidates, candidate => candidate.Title == "Mappable Premiere");
        Assert.Equal(12345, premiere.TvdbId);
        Assert.True(premiere.IsSeriesEpisode);
        Assert.Equal(1, premiere.SeasonNumber);
        Assert.Equal(1, premiere.EpisodeNumber);
        Assert.Equal("nl", premiere.OriginalLanguage);

        var episode = Assert.Single(candidates, candidate => candidate.Title == "Episode Two");
        Assert.Equal(22222, episode.TvdbId);
        Assert.True(episode.IsSeriesEpisode);
        Assert.Equal(1, episode.SeasonNumber);
        Assert.Equal(2, episode.EpisodeNumber);

        var unmappable = Assert.Single(candidates, candidate => candidate.Title == "Unmappable Premiere");
        Assert.Null(unmappable.TmdbId);
        Assert.Null(unmappable.ImdbId);
        Assert.Null(unmappable.TvdbId);
        Assert.True(unmappable.IsSeriesEpisode);
        Assert.Equal("TVmaze schedule", unmappable.Source);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "BE" && !call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "BE" && call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "US" && !call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "GB" && call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "AU" && !call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "" && call.WebSchedule);
    }

    [Fact]
    public async Task GetCandidatesAsync_WhenScheduleDiscoveryDisabledSkipsClientCalls()
    {
        var client = new FakeTvmazeClient();
        var provider = new TvmazeScheduleDiscoveryProvider(
            client,
            Microsoft.Extensions.Options.Options.Create(new TvmazeOptions
            {
                Enabled = true,
                EnableScheduleDiscovery = false,
                ScheduleCountries = ["BE", "US", "GB", "AU"]
            }));

        var candidates = await provider.GetCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 4),
            CancellationToken.None);

        Assert.Empty(candidates);
        Assert.Empty(client.ScheduleCalls);
    }

    [Fact]
    public async Task GetCandidatesAsync_SkipsFailedScheduleRequestAndKeepsOtherCandidates()
    {
        var client = new FakeTvmazeClient
        {
            ThrowForCalls =
            [
                new ScheduleCall(new DateOnly(2026, 5, 4), "BE", false)
            ],
            ScheduleItems =
            [
                new TvmazeScheduleEpisode
                {
                    Season = 1,
                    Number = 1,
                    Airdate = "2026-05-04",
                    Embedded = new TvmazeEmbedded
                    {
                        Show = new TvmazeShow
                        {
                            Name = "Surviving Schedule",
                            Language = "English",
                            Externals = new TvmazeExternals { TheTvdb = 67890 }
                        }
                    }
                }
            ]
        };
        var provider = CreateProvider(client);

        var candidates = await provider.GetCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 4),
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Surviving Schedule", candidate.Title);
        Assert.Equal(67890, candidate.TvdbId);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "BE" && !call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "BE" && call.WebSchedule);
    }

    [Fact]
    public async Task GetCandidatesAsync_SkipsTimedOutScheduleRequestAndKeepsOtherCandidates()
    {
        var client = new FakeTvmazeClient
        {
            ExceptionsForCalls =
            {
                [new ScheduleCall(new DateOnly(2026, 5, 4), "BE", false)] =
                    new OperationCanceledException("TVmaze schedule request timed out.")
            },
            ScheduleItems =
            [
                new TvmazeScheduleEpisode
                {
                    Season = 1,
                    Number = 1,
                    Airdate = "2026-05-04",
                    Embedded = new TvmazeEmbedded
                    {
                        Show = new TvmazeShow
                        {
                            Name = "Timeout Survivor",
                            Language = "English",
                            Externals = new TvmazeExternals { TheTvdb = 11111 }
                        }
                    }
                }
            ]
        };
        var provider = CreateProvider(client);

        var candidates = await provider.GetCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 4),
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("Timeout Survivor", candidate.Title);
        Assert.Equal(11111, candidate.TvdbId);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "BE" && !call.WebSchedule);
        Assert.Contains(client.ScheduleCalls, call => call.Country == "BE" && call.WebSchedule);
    }

    private static TvmazeScheduleDiscoveryProvider CreateProvider(FakeTvmazeClient client)
    {
        return new TvmazeScheduleDiscoveryProvider(
            client,
            Microsoft.Extensions.Options.Options.Create(new TvmazeOptions
            {
                Enabled = true,
                EnableScheduleDiscovery = true,
                ScheduleCountries = ["BE", "US", "GB", "AU"]
            }));
    }

    private sealed class FakeTvmazeClient : ITvmazeClient
    {
        public IReadOnlyList<TvmazeScheduleEpisode> ScheduleItems { get; init; } = [];
        public IReadOnlyCollection<ScheduleCall> ThrowForCalls { get; init; } = [];
        public Dictionary<ScheduleCall, Exception> ExceptionsForCalls { get; init; } = [];
        public List<ScheduleCall> ScheduleCalls { get; } = [];

        public Task<TvmazeShow?> LookupShowAsync(
            int? tvdbId,
            string? imdbId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<TvmazeShow?>(null);
        }

        public Task<TvmazeShow?> SearchShowByNameAsync(
            string title,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<TvmazeShow?>(null);
        }

        public Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(
            int showId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TvmazeShowImage>>([]);
        }

        public Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(
            DateOnly date,
            string? country,
            bool webSchedule,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            ScheduleCalls.Add(new ScheduleCall(date, country, webSchedule));
            if (ThrowForCalls.Contains(new ScheduleCall(date, country, webSchedule)))
            {
                throw new ExternalApiException("TVmaze schedule failed.");
            }

            if (ExceptionsForCalls.TryGetValue(new ScheduleCall(date, country, webSchedule), out var exception))
            {
                throw exception;
            }

            return Task.FromResult(ScheduleItems);
        }

        public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(
            TvmazeUpdateWindow since,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TvmazeShowUpdate>>([]);
        }
    }

    private sealed record ScheduleCall(DateOnly Date, string? Country, bool WebSchedule);
}
