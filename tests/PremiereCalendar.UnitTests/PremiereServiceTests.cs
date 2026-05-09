using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PremiereServiceTests
{
    [Fact]
    public async Task GetPremieresAsync_DeduplicatesSameMediaTypeAndTmdbIdAcrossQueries()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 10,
                    Name = "Shared Premiere",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    VoteAverage = 7.1,
                    VoteCount = 12
                }
            ]
        };
        var service = CreateService(tmdb);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("Shared Premiere", item.Title);
        Assert.Equal("tv:10", item.CanonicalId);
        Assert.Equal(PremiereItemType.SeriesPremiere, item.Type);
        Assert.Equal(PremiereMediaType.Series, item.MediaType);
        var call = Assert.Single(tmdb.TvCalls);
        Assert.Empty(call.Filters.OriginalLanguage);
        Assert.Empty(call.Filters.OriginCountries);
    }

    [Fact]
    public async Task GetPremieresAsync_DefaultsToSeriesEpisodeDiscoveryByDay()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 15,
                    Name = "Daily Episode",
                    FirstAirDate = "2026-05-06",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var service = CreateService(tmdb);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("tv:15:air:20260506", item.CanonicalId);
        Assert.Equal(PremiereItemType.SeriesEpisode, item.Type);
        Assert.Equal(new DateOnly(2026, 5, 6), item.PremiereDate);
        Assert.Equal("TMDb air date", item.EpisodeSource);
        Assert.Equal(7, tmdb.TvCalls.Count);
        Assert.All(tmdb.TvCalls, call => Assert.True(call.Filters.UseEpisodeAirDate));
    }

    [Fact]
    public async Task GetPremieresAsync_UsesUnfilteredTmdbDiscoveryForSeriesAndMovies()
    {
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb);

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Equal(7, tmdb.TvCalls.Count);
        Assert.All(tmdb.TvCalls, tvCall =>
        {
            Assert.True(tvCall.Filters.UseEpisodeAirDate);
            Assert.Empty(tvCall.Filters.OriginalLanguage);
            Assert.Empty(tvCall.Filters.OriginCountries);
        });
        Assert.Empty(tmdb.TvNetworkCalls);

        Assert.Equal(7, tmdb.MovieCalls.Count);
        Assert.All(tmdb.MovieCalls, movieCall =>
        {
            Assert.Empty(movieCall.Filters.OriginalLanguage);
            Assert.Empty(movieCall.Filters.OriginCountries);
        });
    }

    [Fact]
    public async Task GetPremieresAsync_ReportsSourceProgress()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 101,
                    Name = "Progress Show",
                    FirstAirDate = "2026-05-04"
                }
            ],
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 202,
                    Title = "Progress Movie",
                    ReleaseDate = "2026-05-05"
                }
            ]
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(tmdb);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));

        Assert.Equal(2, items.Count);
        Assert.Contains(reports, report => report.SourceName.StartsWith("TMDb series", StringComparison.Ordinal) && report.SourceItemCount == 1);
        Assert.Contains(reports, report => report.SourceName.StartsWith("TMDb movies", StringComparison.Ordinal) && report.SourceItemCount == 1);
        Assert.Contains(reports, report => report is { SourceName: "Complete", IsFinal: true, TotalItemCount: 2 });
    }

    [Fact]
    public async Task GetPremieresAsync_StreamsLargeTmdbBatchesInSmallerEnrichedProgressChunks()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems = Enumerable.Range(1, 45)
                .Select(index => new TmdbTvDiscoverItem
                {
                    Id = index,
                    Name = $"Chunked Show {index}",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                })
                .ToArray()
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            enrichmentProgressBatchSize: 10);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: NewSeriesOnlyFilters());

        Assert.Equal(45, items.Count);
        var tmdbSeriesUpdates = reports
            .Where(report => report.SourceName == "TMDb series"
                && !report.IsFinal
                && report.ProgressText?.Contains("processed", StringComparison.OrdinalIgnoreCase) == true)
            .Select(report => report.CompletedWork ?? 0)
            .ToArray();
        Assert.Equal([10, 20, 30, 40, 45], tmdbSeriesUpdates);
    }

    [Fact]
    public async Task GetPremieresAsync_ReportsTmdbPageProgressDetails()
    {
        var firstBatchItems = Enumerable.Range(1, 12)
            .Select(index => new TmdbTvDiscoverItem
            {
                Id = index,
                Name = $"Paged Show {index}",
                FirstAirDate = "2026-05-04",
                OriginalLanguage = "en",
                OriginCountry = ["US"]
            })
            .ToArray();
        var secondBatchItems = Enumerable.Range(13, 8)
            .Select(index => new TmdbTvDiscoverItem
            {
                Id = index,
                Name = $"Paged Show {index}",
                FirstAirDate = "2026-05-04",
                OriginalLanguage = "en",
                OriginCountry = ["US"]
            })
            .ToArray();
        var tmdb = new FakeTmdbClient
        {
            TvStreamBatches =
            [
                new TmdbDiscoverBatch<TmdbTvDiscoverItem>(1, 1, 2, 20, firstBatchItems),
                new TmdbDiscoverBatch<TmdbTvDiscoverItem>(2, 2, 2, 20, secondBatchItems)
            ]
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            enrichmentProgressBatchSize: 5);

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: NewSeriesOnlyFilters());

        var tmdbSeriesUpdates = reports
            .Where(report => report.SourceName == "TMDb series" && !report.IsFinal)
            .ToArray();

        Assert.Contains(tmdbSeriesUpdates, report =>
            report.ProgressText?.Contains("pages 1-1 of 2", StringComparison.OrdinalIgnoreCase) == true
            && report.CompletedWork == 5
            && report.TotalWork == 20);
        Assert.Contains(tmdbSeriesUpdates, report =>
            report.ProgressText?.Contains("pages 2-2 of 2", StringComparison.OrdinalIgnoreCase) == true
            && report.CompletedWork == 20
            && report.TotalWork == 20);
    }

    [Fact]
    public async Task StreamPremieresAsync_YieldsDiscoverMetadataBeforeDetailEnrichment()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 101,
                    Name = "Fast Metadata Show",
                    FirstAirDate = "2026-05-04",
                    Overview = "Discover overview",
                    PosterPath = "/fast.jpg",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    VoteAverage = 7.1,
                    VoteCount = 12
                }
            ],
            TvDetailDelay = TimeSpan.FromMilliseconds(200)
        };
        var service = CreateService(tmdb);

        await using var enumerator = service.StreamPremieresAsync(
                new DateOnly(2026, 5, 4),
                new DateOnly(2026, 5, 10),
                filters: NewSeriesOnlyFilters(),
                cancellationToken: CancellationToken.None)
            .GetAsyncEnumerator();

        PremiereLoadProgress? firstTmdbUpdate = null;
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.SourceName == "TMDb series"
                && enumerator.Current.SourceItemCount > 0)
            {
                firstTmdbUpdate = enumerator.Current;
                break;
            }
        }

        Assert.NotNull(firstTmdbUpdate);
        var item = Assert.Single(firstTmdbUpdate.SourceItems);
        Assert.Equal("Fast Metadata Show", item.Title);
        Assert.Equal("Discover overview", item.Overview);
        Assert.Equal("TMDb poster", item.ImageSource);
        Assert.Null(item.TrailerUrl);
        Assert.Contains("metadata", firstTmdbUpdate.ProgressText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamPremieresAsync_DisposesCleanlyWhileAnotherSourceIsStillMoving()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 101,
                    Name = "Fast Source Show",
                    FirstAirDate = "2026-05-04"
                }
            ]
        };
        var slowProvider = new FakeDiscoveryProvider
        {
            DisplayName = "Slow external source",
            Delay = TimeSpan.FromSeconds(10)
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [slowProvider],
            sourceFetchConcurrency: 2);
        using var cancellation = new CancellationTokenSource();
        var enumerator = service.StreamPremieresAsync(
                new DateOnly(2026, 5, 4),
                new DateOnly(2026, 5, 10),
                filters: NewSeriesOnlyFilters(),
                cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.SourceName == "TMDb series"
                && enumerator.Current.SourceItemCount > 0)
            {
                break;
            }
        }

        cancellation.Cancel();
        var exception = await Record.ExceptionAsync(async () => await enumerator.DisposeAsync().AsTask());

        Assert.Null(exception);
    }

    [Fact]
    public async Task StreamPremieresAsync_PrioritizesSelectedDaySources()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 105,
                    Name = "Tuesday First",
                    FirstAirDate = "2026-05-05"
                },
                new TmdbTvDiscoverItem
                {
                    Id = 104,
                    Name = "Monday Second",
                    FirstAirDate = "2026-05-04"
                }
            ]
        };
        var filters = new CalendarFilters
        {
            PriorityDate = new DateOnly(2026, 5, 5)
        };
        filters.SeriesFilters.SeriesDateMode = SeriesDateMode.AllEpisodes;
        var service = CreateService(
            tmdb,
            sourceFetchConcurrency: 1);

        await using var enumerator = service.StreamPremieresAsync(
                new DateOnly(2026, 5, 4),
                new DateOnly(2026, 5, 10),
                filters: filters,
                cancellationToken: CancellationToken.None)
            .GetAsyncEnumerator();

        PremiereLoadProgress? firstTmdbUpdate = null;
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.SourceName.StartsWith("TMDb series", StringComparison.Ordinal)
                && enumerator.Current.SourceItemCount > 0)
            {
                firstTmdbUpdate = enumerator.Current;
                break;
            }
        }

        Assert.NotNull(firstTmdbUpdate);
        Assert.Contains("Tue 05 May", firstTmdbUpdate.SourceName);
        Assert.Equal("Tuesday First", Assert.Single(firstTmdbUpdate.SourceItems).Title);
    }

    [Fact]
    public async Task StreamPremieresAsync_ReusesWeekCacheForFreshEnrichmentDuringForcedRefresh()
    {
        var cachedItem = new PremiereItem
        {
            CanonicalId = "tv:110",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 110,
            ImdbId = "tt110",
            TvdbId = 1110,
            Title = "Cached Fast Show",
            PremiereDate = new DateOnly(2026, 5, 4),
            OriginalLanguage = "en",
            OriginCountries = ["US"],
            SourceNames = ["Cached Network"],
            Sources = [new PremiereSource { Name = "Cached Network", Kind = "network" }],
            Genres = ["Drama"],
            GenreIds = [18],
            RuntimeMinutes = 44,
            TrailerUrl = "https://www.youtube.com/watch?v=cached",
            PosterUrl = "https://image.tmdb.org/t/p/w185/cached.jpg",
            ImageSource = "TMDb poster",
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 110,
                    Name = "Cached Fast Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    PosterPath = "/fresh.jpg"
                }
            ],
            TvDetailDelay = TimeSpan.FromSeconds(5)
        };
        var cache = new FakeCalendarCache
        {
            Items = [cachedItem]
        };
        var service = CreateService(tmdb, calendarCache: cache);

        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            forceRefresh: true,
            filters: NewSeriesOnlyFilters(),
            cancellationToken: CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Empty(tmdb.TvDetailCalls);
        var complete = Assert.Single(updates, update => update.IsFinal);
        var item = Assert.Single(complete.Items);
        Assert.Equal("Cached Network", Assert.Single(item.SourceNames));
        Assert.Equal("https://image.tmdb.org/t/p/w185/fresh.jpg", item.PosterUrl);
        Assert.Equal("https://www.youtube.com/watch?v=cached", item.TrailerUrl);
    }

    [Fact]
    public async Task StreamPremieresAsync_UsesExpiredWeekCacheAsEnrichmentSeedAfterFreshMiss()
    {
        var cachedItem = new PremiereItem
        {
            CanonicalId = "tv:111",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 111,
            ImdbId = "tt111",
            TvdbId = 1111,
            Title = "Stale Fast Show",
            PremiereDate = new DateOnly(2026, 5, 4),
            OriginalLanguage = "en",
            OriginCountries = ["US"],
            SourceNames = ["Cached Network"],
            Sources = [new PremiereSource { Name = "Cached Network", Kind = "network" }],
            Genres = ["Drama"],
            GenreIds = [18],
            RuntimeMinutes = 44,
            TrailerUrl = "https://www.youtube.com/watch?v=stale",
            PosterUrl = "https://image.tmdb.org/t/p/w185/stale.jpg",
            ImageSource = "TMDb poster",
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 111,
                    Name = "Stale Fast Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    PosterPath = "/fresh.jpg"
                }
            ]
        };
        var cache = new FakeCalendarCache
        {
            ExpiredItems = [cachedItem]
        };
        var service = CreateService(tmdb, calendarCache: cache);

        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            filters: NewSeriesOnlyFilters(),
            cancellationToken: CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Empty(tmdb.TvDetailCalls);
        var complete = Assert.Single(updates, update => update.IsFinal);
        var item = Assert.Single(complete.Items);
        Assert.Equal("Cached Network", Assert.Single(item.SourceNames));
        Assert.Equal("https://image.tmdb.org/t/p/w185/fresh.jpg", item.PosterUrl);
        Assert.Equal("https://www.youtube.com/watch?v=stale", item.TrailerUrl);
    }

    [Fact]
    public async Task StreamPremieresAsync_ReusesWeekCacheForExternalCandidateResolutionDuringForcedRefresh()
    {
        var cachedItem = new PremiereItem
        {
            CanonicalId = "tv:120:s01e01",
            Type = PremiereItemType.SeriesEpisode,
            MediaType = PremiereMediaType.Series,
            TmdbId = 120,
            TvdbId = 2120,
            Title = "Cached External Show",
            PremiereDate = new DateOnly(2026, 5, 4),
            OriginalLanguage = "en",
            OriginCountries = ["US"],
            SourceNames = ["Cached Schedule"],
            Sources = [new PremiereSource { Name = "Cached Schedule", Kind = "network" }],
            SeasonNumber = 1,
            EpisodeNumber = 1,
            EpisodeTitle = "Pilot",
            EpisodeSource = "Cached Schedule",
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Cached External Show",
                    null,
                    null,
                    2120,
                    "Fresh Schedule",
                    IsSeriesEpisode: true,
                    EpisodeTitle: "Pilot",
                    SeasonNumber: 1,
                    EpisodeNumber: 1)
            ]
        };
        var tmdb = new FakeTmdbClient();
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery],
            calendarCache: new FakeCalendarCache { Items = [cachedItem] });

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            forceRefresh: true);

        var item = Assert.Single(items);
        Assert.Equal("tv:120:s01e01", item.CanonicalId);
        Assert.Empty(tmdb.FindCalls);
        Assert.Equal("Fresh Schedule", item.EpisodeSource);
    }

    [Fact]
    public async Task GetPremieresAsync_StreamsExternalProviderResultsAsEachProviderCompletes()
    {
        var fastDiscovery = new FakeDiscoveryProvider
        {
            DisplayName = "Fast provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Fast External Show",
                    81,
                    null,
                    null,
                    "Fast")
            ]
        };
        var slowDiscovery = new FakeDiscoveryProvider
        {
            DisplayName = "Slow provider",
            Delay = TimeSpan.FromMilliseconds(100),
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Slow External Show",
                    82,
                    null,
                    null,
                    "Slow")
            ]
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            new FakeTmdbClient(),
            discoveryProviders: [slowDiscovery, fastDiscovery],
            enrichmentProgressBatchSize: 1);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));

        Assert.Equal(2, items.Count);
        var externalUpdates = reports
            .Where(report => (report.SourceName == "Fast provider" || report.SourceName == "Slow provider") && !report.IsFinal)
            .Select(report => report.SourceName)
            .ToArray();
        Assert.Contains("Fast provider", externalUpdates);
        Assert.Contains("Slow provider", externalUpdates);
        Assert.Equal("Fast provider", externalUpdates.First());
        Assert.DoesNotContain(reports, report => report.SourceName == "External calendars");
    }

    [Fact]
    public async Task GetPremieresAsync_ReportsExternalCandidateResolutionProgress()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Progress provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "External Progress One",
                    81,
                    null,
                    null,
                    "Fast"),
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 5),
                    "External Progress Two",
                    82,
                    null,
                    null,
                    "Fast")
            ]
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            new FakeTmdbClient(),
            discoveryProviders: [discovery],
            enrichmentProgressBatchSize: 1);

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));

        var externalUpdates = reports
            .Where(report => report.SourceName == "Progress provider" && !report.IsFinal)
            .ToArray();

        Assert.Contains(externalUpdates, report =>
            report.ProgressText?.Contains("resolved 1 of 2 candidates", StringComparison.OrdinalIgnoreCase) == true
            && report.CompletedWork == 1
            && report.TotalWork == 2);
        Assert.Contains(externalUpdates, report =>
            report.ProgressText?.Contains("resolved 2 of 2 candidates", StringComparison.OrdinalIgnoreCase) == true
            && report.CompletedWork == 2
            && report.TotalWork == 2);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsExternalCandidatesWithKnownMismatchedLanguageBeforeTmdbResolution()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Language provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "English External",
                    null,
                    null,
                    81,
                    "Language provider",
                    OriginalLanguage: "en"),
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Japanese External",
                    null,
                    null,
                    82,
                    "Language provider",
                    OriginalLanguage: "ja")
            ]
        };
        var tmdb = new FakeTmdbClient();
        tmdb.FindResults[(PremiereMediaType.Series, "tvdb_id", "81")] = 181;
        tmdb.FindResults[(PremiereMediaType.Series, "tvdb_id", "82")] = 182;
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery],
            enrichmentProgressBatchSize: 1);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                OriginalLanguages = { "en" }
            }
        };

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Single(items);
        Assert.Contains(tmdb.FindCalls, call => call.ExternalId == "81");
        Assert.DoesNotContain(tmdb.FindCalls, call => call.ExternalId == "82");
    }

    [Fact]
    public async Task GetPremieresAsync_ReportsWhenExternalCandidatesDoNotMatchRequestFilters()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Language provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Japanese External",
                    null,
                    null,
                    82,
                    "Language provider",
                    OriginalLanguage: "ja")
            ]
        };
        var tmdb = new FakeTmdbClient();
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                OriginalLanguages = { "en" }
            }
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: filters);

        var providerReport = Assert.Single(reports, report => report.SourceName == "Language provider");
        Assert.Equal(0, providerReport.SourceItemCount);
        Assert.Equal(1, providerReport.CompletedWork);
        Assert.Equal(1, providerReport.TotalWork);
        Assert.Contains("0 of 1 candidates matched request filters", providerReport.ProgressText);
        Assert.Empty(tmdb.FindCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_ReportsExternalProviderCompletionWhenNoCardsMatch()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "TVmaze schedules",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Japanese External",
                    null,
                    null,
                    82,
                    "TVmaze",
                    OriginalLanguage: "ja")
            ]
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            new FakeTmdbClient(),
            discoveryProviders: [discovery]);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                OriginalLanguages = { "en" }
            }
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: filters);

        Assert.Contains(
            reports,
            report => report.SourceName == "TVmaze schedules"
                && report.Phase == "complete"
                && report.SourceItemCount == 0
                && report.ProgressText?.Contains("Done", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task GetPremieresAsync_TrustsExternalCandidateTmdbIdWithoutExtraExternalIdResolution()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Trakt",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Trakt Show",
                    84,
                    "tt0000084",
                    9000,
                    "Trakt",
                    OriginalLanguage: "en")
            ]
        };
        var tmdb = new FakeTmdbClient();
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = true, ShowMovies = false });

        var item = Assert.Single(items);
        Assert.Equal(84, item.TmdbId);
        Assert.Empty(tmdb.FindCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_NewSeriesOnlyAcceptsExternalSeasonOneEpisodeOneAsPremiere()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Schedule provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "External Premiere",
                    90,
                    null,
                    null,
                    "Schedule provider",
                    IsSeriesEpisode: true,
                    EpisodeTitle: "Pilot",
                    SeasonNumber: 1,
                    EpisodeNumber: 1)
            ]
        };
        var service = CreateService(
            new FakeTmdbClient(),
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal(PremiereItemType.SeriesPremiere, item.Type);
        Assert.Equal("External Premiere", item.Title);
        Assert.Equal("Pilot", item.EpisodeTitle);
        Assert.Equal(1, item.SeasonNumber);
        Assert.Equal(1, item.EpisodeNumber);
        Assert.Equal("Schedule provider", item.EpisodeSource);
    }

    [Fact]
    public async Task GetPremieresAsync_NewSeriesOnlySkipsExternalLaterEpisodesBeforeTmdbResolution()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Schedule provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Later Episode",
                    null,
                    null,
                    82,
                    "Schedule provider",
                    IsSeriesEpisode: true,
                    EpisodeTitle: "Episode Two",
                    SeasonNumber: 1,
                    EpisodeNumber: 2)
            ]
        };
        var tmdb = new FakeTmdbClient();
        tmdb.FindResults[(PremiereMediaType.Series, "tvdb_id", "82")] = 182;
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Empty(items);
        Assert.Empty(tmdb.FindCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_NewSeriesOnlySkipsExternalPilotWhenSeriesPremieredOnAnotherDate()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Schedule provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Delayed Pilot Airing",
                    90,
                    null,
                    null,
                    "Schedule provider",
                    IsSeriesEpisode: true,
                    EpisodeTitle: "Pilot",
                    SeasonNumber: 1,
                    EpisodeNumber: 1,
                    SeriesPremiereDate: new DateOnly(2026, 1, 10))
            ]
        };
        var tmdb = new FakeTmdbClient();
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Empty(items);
        Assert.Empty(tmdb.FindCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsExternalCandidateWhenExternalIdsResolveToDifferentTmdbItems()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Conflicting provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Conflicting IDs",
                    null,
                    "tt0000001",
                    82,
                    "Conflicting provider",
                    IsSeriesEpisode: true,
                    EpisodeTitle: "Pilot",
                    SeasonNumber: 1,
                    EpisodeNumber: 1)
            ]
        };
        var tmdb = new FakeTmdbClient();
        tmdb.FindResults[(PremiereMediaType.Series, "tvdb_id", "82")] = 182;
        tmdb.FindResults[(PremiereMediaType.Series, "imdb_id", "tt0000001")] = 190;
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        Assert.Empty(items);
        Assert.Contains(tmdb.FindCalls, call => call.ExternalSource == "tvdb_id");
        Assert.Contains(tmdb.FindCalls, call => call.ExternalSource == "imdb_id");
    }

    [Fact]
    public async Task GetPremieresAsync_CompletesWhenOneSourceTimesOut()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 101,
                    Name = "Fast Show",
                    FirstAirDate = "2026-05-04"
                }
            ],
            MovieDelay = TimeSpan.FromSeconds(5)
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(tmdb, sourceTimeoutSeconds: 1);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add));

        Assert.Equal("Fast Show", Assert.Single(items).Title);
        Assert.Contains(reports, report => report.SourceName.StartsWith("TMDb movies", StringComparison.Ordinal)
            && report.SourceItemCount == 0
            && report.Phase == "complete"
            && report.ProgressText?.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(reports, report => report is { SourceName: "Complete", IsFinal: true, TotalItemCount: 1 });
    }

    [Fact]
    public async Task GetPremieresAsync_DoesNotPersistPartialResultsWhenSourceTimesOut()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 101,
                    Name = "Fast Show",
                    FirstAirDate = "2026-05-04"
                }
            ],
            MovieDelay = TimeSpan.FromSeconds(5)
        };
        var cache = new FakeCalendarCache();
        var service = CreateService(tmdb, calendarCache: cache, sourceTimeoutSeconds: 1);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        Assert.Equal("Fast Show", Assert.Single(items).Title);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task StreamPremieresAsync_ComposesAllViewFromSharedSeriesAndMovieCaches()
    {
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var filters = new CalendarFilters { ShowSeries = true, ShowMovies = true };
        var seriesKey = CacheKeyForPageMode(filters, CalendarPageMode.Series);
        var movieKey = CacheKeyForPageMode(filters, CalendarPageMode.Movies);
        var cache = new FakeCalendarCache();
        cache.ItemsByKey[(start, end, seriesKey)] =
        [
            new PremiereItem
            {
                CanonicalId = "series:101",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 101,
                Title = "Cached Series",
                PremiereDate = start,
                OriginalLanguage = "en"
            }
        ];
        cache.ItemsByKey[(start, end, movieKey)] =
        [
            new PremiereItem
            {
                CanonicalId = "movie:201",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 201,
                Title = "Cached Movie",
                PremiereDate = start.AddDays(1),
                OriginalLanguage = "en"
            }
        ];
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb, calendarCache: cache);

        var items = await service.GetPremieresAsync(start, end, CancellationToken.None, filters: filters);

        Assert.Equal(["Cached Series", "Cached Movie"], items.Select(item => item.Title));
        Assert.Empty(tmdb.TvCalls);
        Assert.Empty(tmdb.MovieCalls);
    }

    [Fact]
    public async Task StreamPremieresAsync_AllViewFetchesOnlyMissingSharedMediaCache()
    {
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var filters = new CalendarFilters { ShowSeries = true, ShowMovies = true };
        var seriesKey = CacheKeyForPageMode(filters, CalendarPageMode.Series);
        var cache = new FakeCalendarCache();
        cache.ItemsByKey[(start, end, seriesKey)] =
        [
            new PremiereItem
            {
                CanonicalId = "series:101",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 101,
                Title = "Cached Series",
                PremiereDate = start,
                OriginalLanguage = "en"
            }
        ];
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 201,
                    Title = "Fresh Movie",
                    OriginalTitle = "Fresh Movie",
                    ReleaseDate = "2026-05-05",
                    PrimaryReleaseDate = "2026-05-05",
                    OriginalLanguage = "en"
                }
            ]
        };
        var service = CreateService(tmdb, calendarCache: cache);

        var progress = new List<PremiereLoadProgress>();
        var items = await service.GetPremieresAsync(
            start,
            end,
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(progress.Add),
            filters: filters);

        Assert.Equal(["Cached Series", "Fresh Movie"], items.Select(item => item.Title));
        Assert.Empty(tmdb.TvCalls);
        Assert.NotEmpty(tmdb.MovieCalls);
        Assert.Contains(progress, update => update.FromCache && update.Items.Any(item => item.Title == "Cached Series"));
        Assert.Contains(cache.SetCalls, call => call.CacheKey == seriesKey);
    }

    [Fact]
    public async Task GetPremieresAsync_WritesAllViewResultsToSharedSeriesAndMovieCaches()
    {
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var filters = new CalendarFilters { ShowSeries = true, ShowMovies = true };
        var seriesKey = CacheKeyForPageMode(filters, CalendarPageMode.Series);
        var movieKey = CacheKeyForPageMode(filters, CalendarPageMode.Movies);
        var allKey = PremiereDiscoveryCriteria.FromFilters(filters).CacheKey();
        var cache = new FakeCalendarCache();
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 101,
                    Name = "Fresh Series",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ],
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 201,
                    Title = "Fresh Movie",
                    ReleaseDate = "2026-05-05",
                    PrimaryReleaseDate = "2026-05-05",
                    OriginalLanguage = "en"
                }
            ]
        };
        var service = CreateService(tmdb, calendarCache: cache);

        var items = await service.GetPremieresAsync(start, end, CancellationToken.None, filters: filters);

        Assert.Contains(items, item => item.Title == "Fresh Series");
        Assert.Contains(items, item => item.Title == "Fresh Movie");
        var seriesCall = Assert.Single(cache.SetCalls, call => call.CacheKey == seriesKey);
        var movieCall = Assert.Single(cache.SetCalls, call => call.CacheKey == movieKey);
        Assert.DoesNotContain(cache.SetCalls, call => call.CacheKey == allKey);
        Assert.All(seriesCall.Items, item => Assert.Equal(PremiereMediaType.Series, item.MediaType));
        Assert.All(movieCall.Items, item => Assert.Equal(PremiereMediaType.Movie, item.MediaType));
    }

    [Fact]
    public async Task GetPremieresAsync_DoesNotReuseCacheAcrossDifferentLegacyLanguageFilters()
    {
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var cache = new FakeCalendarCache();
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 301,
                    Name = "English Cache Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en"
                },
                new TmdbTvDiscoverItem
                {
                    Id = 302,
                    Name = "Dutch Cache Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "nl"
                }
            ]
        };
        var service = CreateService(tmdb, calendarCache: cache);

        var englishItems = await service.GetPremieresAsync(
            start,
            end,
            CancellationToken.None,
            filters: new CalendarFilters
            {
                ShowSeries = true,
                ShowMovies = false,
                Language = LanguageFilter.English
            });
        var dutchItems = await service.GetPremieresAsync(
            start,
            end,
            CancellationToken.None,
            filters: new CalendarFilters
            {
                ShowSeries = true,
                ShowMovies = false,
                Language = LanguageFilter.Dutch
            });

        Assert.Equal("English Cache Show", Assert.Single(englishItems).Title);
        Assert.Equal("Dutch Cache Show", Assert.Single(dutchItems).Title);
        Assert.Equal(2, cache.SetCalls.Select(call => call.CacheKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GetPremieresAsync_DoesNotReuseCacheAcrossDifferentLocalScoreRanges()
    {
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var cache = new FakeCalendarCache();
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 401,
                    Title = "High IMDb Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                },
                new TmdbMovieDiscoverItem
                {
                    Id = 402,
                    Title = "Mid IMDb Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ]
        };
        tmdb.MovieDetailsById[401] = new TmdbDetailsWithExtras
        {
            Id = 401,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000401" }
        };
        tmdb.MovieDetailsById[402] = new TmdbDetailsWithExtras
        {
            Id = 402,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000402" }
        };
        var omdb = new FakeOmdbClient();
        omdb.ItemsByImdbId["tt0000401"] = new OmdbItem { Response = "True", ImdbRating = "8.6" };
        omdb.ItemsByImdbId["tt0000402"] = new OmdbItem { Response = "True", ImdbRating = "6.2" };
        var service = CreateService(tmdb, omdb: omdb, calendarCache: cache);

        var highScoreItems = await service.GetPremieresAsync(
            start,
            end,
            CancellationToken.None,
            filters: new CalendarFilters
            {
                ShowSeries = false,
                ShowMovies = true,
                ScoreSource = ScoreSource.Imdb,
                MinScore = 8
            });
        var midScoreItems = await service.GetPremieresAsync(
            start,
            end,
            CancellationToken.None,
            filters: new CalendarFilters
            {
                ShowSeries = false,
                ShowMovies = true,
                ScoreSource = ScoreSource.Imdb,
                MinScore = 5
            });

        Assert.Equal("High IMDb Movie", Assert.Single(highScoreItems).Title);
        Assert.Equal(["High IMDb Movie", "Mid IMDb Movie"], midScoreItems.Select(item => item.Title).Order());
        Assert.Equal(2, cache.SetCalls.Select(call => call.CacheKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task GetPremieresAsync_LimitsDaySourceFetchConcurrency()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieDelay = TimeSpan.FromMilliseconds(200)
        };
        var service = CreateService(tmdb, sourceFetchConcurrency: 2);
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Equal(7, tmdb.MovieCalls.Count);
        Assert.InRange(tmdb.MaxConcurrentMovieDiscoveries, 1, 2);
    }

    [Fact]
    public async Task GetPremieresAsync_StartsExternalProvidersWhenPriorityTmdbMovieSlicesAreStillLoading()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieDelay = TimeSpan.FromSeconds(5)
        };
        var externalProvider = new FakeDiscoveryProvider();
        var service = CreateService(
            tmdb,
            discoveryProviders: [externalProvider],
            sourceTimeoutSeconds: 10,
            sourceFetchConcurrency: 2);
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true,
            PriorityDate = new DateOnly(2026, 5, 4),
            MovieFilters =
            {
                OriginalLanguages = ["en", "nl"]
            }
        };
        using var cancellation = new CancellationTokenSource();

        var loadTask = service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            cancellation.Token,
            filters: filters);
        var startedTask = await Task.WhenAny(
            externalProvider.Started.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500)));

        await cancellation.CancelAsync();
        try
        {
            await loadTask;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Same(externalProvider.Started.Task, startedTask);
    }

    [Fact]
    public async Task GetPremieresAsync_CapsTmdbMovieSourceConcurrencyBelowBroadSourceConcurrency()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieDelay = TimeSpan.FromMilliseconds(200)
        };
        var service = CreateService(tmdb, sourceFetchConcurrency: 8);
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Equal(7, tmdb.MovieCalls.Count);
        Assert.InRange(tmdb.MaxConcurrentMovieDiscoveries, 1, 2);
    }

    [Fact]
    public async Task GetPremieresAsync_DoesNotStartTvmazeScheduleDiscoveryForMovieOnlyLoads()
    {
        var tmdb = new FakeTmdbClient();
        var tvmaze = new FakeTvmazeClient();
        var service = CreateService(
            tmdb,
            tvmaze,
            discoveryProviders:
            [
                new TvmazeScheduleDiscoveryProvider(
                    tvmaze,
                    Microsoft.Extensions.Options.Options.Create(new TvmazeOptions
                    {
                        Enabled = true,
                        EnableScheduleDiscovery = true,
                        ScheduleCountries = ["US"]
                    }))
            ]);
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Empty(tvmaze.ScheduleCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_PassesSavedFiltersToTmdbDiscover()
    {
        var tmdb = new FakeTmdbClient
        {
            KeywordResults = [new TmdbKeyword { Id = 900, Name = "crime" }]
        };
        var service = CreateService(tmdb);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SortMode = PremiereSortMode.Score,
            SortDirection = SortDirection.Descending,
            ScoreSource = ScoreSource.Tmdb,
            MinScore = 6.5,
            MaxScore = 9,
            MinVoteCount = 25,
            SeriesFilters =
            {
                OriginalLanguages = ["nl"],
                GenreIds = [18],
                WatchRegion = "BE",
                SelectedSources = [SourceFilterValue.Provider(337, "Disney Plus")],
                MonetizationTypes = ["flatrate"],
                RuntimeMinMinutes = 20,
                RuntimeMaxMinutes = 80,
                KeywordText = "crime"
            }
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Equal(7, tmdb.TvCalls.Count);
        var call = tmdb.TvCalls.First();
        Assert.Empty(tmdb.MovieCalls);
        Assert.True(call.Filters.UseEpisodeAirDate);
        Assert.Equal("nl", call.Filters.OriginalLanguage);
        Assert.Equal("first_air_date.asc", call.Filters.SortBy);
        Assert.Equal([18], call.Filters.GenreIds);
        Assert.Equal("BE", call.Filters.WatchRegion);
        Assert.Equal([337], call.Filters.WatchProviderIds);
        Assert.Equal(["flatrate"], call.Filters.WatchMonetizationTypes);
        Assert.Equal(6.5, call.Filters.MinVoteAverage);
        Assert.Equal(9, call.Filters.MaxVoteAverage);
        Assert.Equal(25, call.Filters.MinVoteCount);
        Assert.Equal(20, call.Filters.RuntimeMinMinutes);
        Assert.Equal(80, call.Filters.RuntimeMaxMinutes);
        Assert.Equal([900], call.Filters.KeywordIds);
    }

    [Fact]
    public async Task GetPremieresAsync_FansOutTmdbDiscoveryForMultipleOriginalLanguages()
    {
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SeriesDateMode = SeriesDateMode.NewSeriesOnly,
                OriginalLanguages = ["en", "nl"]
            }
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Equal(["en", "nl"], tmdb.TvCalls.Select(call => call.Filters.OriginalLanguage).Order());
        Assert.Empty(tmdb.MovieCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_MergesNewSeriesResultsAcrossMultipleOriginalLanguages()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItemsByOriginalLanguage =
            {
                ["en"] =
                [
                    new TmdbTvDiscoverItem
                    {
                        Id = 101,
                        Name = "English Premiere",
                        FirstAirDate = "2026-05-04",
                        OriginalLanguage = "en",
                        OriginCountry = ["US"]
                    }
                ],
                ["nl"] =
                [
                    new TmdbTvDiscoverItem
                    {
                        Id = 102,
                        Name = "Dutch Premiere",
                        FirstAirDate = "2026-05-04",
                        OriginalLanguage = "nl",
                        OriginCountry = ["NL"]
                    }
                ]
            }
        };
        var service = CreateService(tmdb);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SeriesDateMode = SeriesDateMode.NewSeriesOnly,
                OriginalLanguages = ["en", "nl"]
            }
        };

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Equal(["Dutch Premiere", "English Premiere"], items.Select(item => item.Title).Order());
    }

    [Fact]
    public async Task GetPremieresAsync_FansOutEpisodeDiscoveryPerDayForMultipleOriginalLanguages()
    {
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb);
        var filters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SeriesDateMode = SeriesDateMode.AllEpisodes,
                OriginalLanguages = ["en", "nl"]
            }
        };

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: filters);

        Assert.Equal(14, tmdb.TvCalls.Count);
        Assert.Equal(7, tmdb.TvCalls.Count(call => call.Filters.OriginalLanguage == "en"));
        Assert.Equal(7, tmdb.TvCalls.Count(call => call.Filters.OriginalLanguage == "nl"));
        Assert.All(tmdb.TvCalls, call => Assert.True(call.Filters.UseEpisodeAirDate));
        Assert.Empty(tmdb.MovieCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_UsesTvmazeTitleSearchForSeriesArtworkWhenIdsAreMissing()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 11,
                    Name = "No Poster Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    VoteAverage = 6.5,
                    VoteCount = 4
                }
            ]
        };
        var tvmaze = new FakeTvmazeClient
        {
            TitleSearchResult = new TvmazeShow
            {
                Name = "No Poster Show",
                Image = new TvmazeImage
                {
                    Original = "https://static.tvmaze.com/uploads/images/original_untouched/1/1.jpg"
                }
            }
        };
        var service = CreateService(tmdb, tvmaze: tvmaze);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("https://static.tvmaze.com/uploads/images/original_untouched/1/1.jpg", item.PosterUrl);
        Assert.Equal("TVmaze image", item.ImageSource);
        Assert.Contains(tvmaze.SearchCalls, call => call.Title == "No Poster Show");
    }

    [Fact]
    public async Task GetPremieresAsync_ForwardsForceRefreshToSourceClients()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 12,
                    Name = "Force Refresh Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    VoteAverage = 7.2,
                    VoteCount = 9
                }
            ]
        };
        var tvmaze = new FakeTvmazeClient
        {
            TitleSearchResult = new TvmazeShow { Name = "Force Refresh Show" }
        };
        var service = CreateService(tmdb, tvmaze: tvmaze);

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            forceRefresh: true);

        Assert.All(tmdb.TvCalls, call => Assert.True(call.ForceRefresh));
        Assert.All(tmdb.TvDetailCalls, call => Assert.True(call.ForceRefresh));
        Assert.All(tvmaze.SearchCalls, call => Assert.True(call.ForceRefresh));
    }

    [Fact]
    public async Task GetPremieresAsync_KeepsExternalDiscoveryRowsThatCannotResolveToTmdbAsUnverified()
    {
        var tmdb = new FakeTmdbClient();
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Unmapped Show",
                    null,
                    "tt-unmapped",
                    9000,
                    "Fake")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal(PremiereVerificationState.Unverified, item.VerificationState);
        Assert.StartsWith("unverified:series:", item.CanonicalId, StringComparison.Ordinal);
        Assert.Equal("Unmapped Show", item.Title);
        Assert.Equal(new DateOnly(2026, 5, 4), item.PremiereDate);
        Assert.Equal(0, item.TmdbId);
        Assert.Equal("Could not match to TMDb yet", item.VerificationNote);
        Assert.Contains("Fake", item.SourceNames);
        Assert.Contains(tmdb.FindCalls, call => call.ExternalSource == "tvdb_id" && call.ExternalId == "9000");
        Assert.Contains(tmdb.FindCalls, call => call.ExternalSource == "imdb_id" && call.ExternalId == "tt-unmapped");
    }

    [Fact]
    public async Task GetPremieresAsync_CollapsesEquivalentUnverifiedExternalCandidatesAndUnionsSources()
    {
        var tmdb = new FakeTmdbClient();
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Unmapped Movie",
                    null,
                    null,
                    null,
                    "Watchmode",
                    ExternalProviderId: "watchmode-1"),
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Unmapped Movie",
                    null,
                    null,
                    null,
                    "Trakt",
                    ExternalProviderId: "trakt-2")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal(PremiereVerificationState.Unverified, item.VerificationState);
        Assert.Equal("Unmapped Movie", item.Title);
        Assert.Contains("Watchmode", item.SourceNames);
        Assert.Contains("Trakt", item.SourceNames);
        Assert.Contains(item.Sources, source => source.Name == "Watchmode" && source.Kind == "schedule");
        Assert.Contains(item.Sources, source => source.Name == "Trakt" && source.Kind == "schedule");
    }

    [Fact]
    public async Task GetPremieresAsync_SuppressesUnverifiedExternalCandidateMatchingVerifiedTitleAndDate()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 72,
                    Title = "Shared Title",
                    OriginalTitle = "Shared Title",
                    ReleaseDate = "2026-05-04",
                    PrimaryReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Shared Title",
                    null,
                    null,
                    null,
                    "Watchmode")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal(PremiereVerificationState.Verified, item.VerificationState);
        Assert.Equal("movie:72", item.CanonicalId);
        Assert.Contains("Watchmode", item.SourceNames);
    }

    [Fact]
    public async Task GetPremieresAsync_SuppressesUnverifiedExternalCandidateMatchingVerifiedImdbId()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 76,
                    Title = "Verified IMDb Movie",
                    OriginalTitle = "Verified IMDb Movie",
                    ReleaseDate = "2026-05-04",
                    PrimaryReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ]
        };
        tmdb.MovieDetailsById[76] = new TmdbDetailsWithExtras
        {
            Id = 76,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt-shared-imdb" }
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Provider IMDb Movie",
                    null,
                    "tt-shared-imdb",
                    null,
                    "Watchmode")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal(PremiereVerificationState.Verified, item.VerificationState);
        Assert.Equal("movie:76", item.CanonicalId);
        Assert.Equal("tt-shared-imdb", item.ImdbId);
        Assert.Contains("Watchmode", item.SourceNames);
    }

    [Fact]
    public async Task GetPremieresAsync_StrictTitleSearchResolvesSingleExternalCandidateMatch()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieTitleSearchResults =
            [
                new TmdbTitleSearchResult
                {
                    Id = 73,
                    Title = "Fallback Movie",
                    OriginalTitle = "Fallback Movie",
                    ReleaseDate = "2026-05-04"
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Fallback Movie",
                    null,
                    null,
                    null,
                    "Watchmode")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal(PremiereVerificationState.Verified, item.VerificationState);
        Assert.Equal("movie:73", item.CanonicalId);
        Assert.Equal(73, item.TmdbId);
        Assert.Contains(tmdb.TitleSearchCalls, call =>
            call.MediaType == PremiereMediaType.Movie
            && call.Query == "Fallback Movie"
            && call.Year == 2026);
    }

    [Fact]
    public async Task GetPremieresAsync_StrictTitleSearchKeepsAmbiguousExternalCandidateUnverified()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieTitleSearchResults =
            [
                new TmdbTitleSearchResult
                {
                    Id = 74,
                    Title = "Ambiguous Movie",
                    OriginalTitle = "Ambiguous Movie",
                    ReleaseDate = "2026-05-04"
                },
                new TmdbTitleSearchResult
                {
                    Id = 75,
                    Title = "Ambiguous Movie",
                    OriginalTitle = "Ambiguous Movie",
                    ReleaseDate = "2026-07-01"
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Ambiguous Movie",
                    null,
                    null,
                    null,
                    "Watchmode")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal(PremiereVerificationState.Unverified, item.VerificationState);
        Assert.Equal(0, item.TmdbId);
        Assert.StartsWith("unverified:movie:", item.CanonicalId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPremieresAsync_WritesUnverifiedExternalCardsToWeekCache()
    {
        var cache = new FakeCalendarCache();
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Cached Unverified Movie",
                    null,
                    null,
                    null,
                    "Watchmode")
            ]
        };
        var service = CreateService(
            new FakeTmdbClient(),
            discoveryProviders: [discovery],
            calendarCache: cache);

        await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var setCall = Assert.Single(cache.SetCalls);
        var item = Assert.Single(setCall.Items);
        Assert.Equal(PremiereVerificationState.Unverified, item.VerificationState);
        Assert.Equal("Cached Unverified Movie", item.Title);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsTimedOutExternalIdResolutionWithoutDroppingOtherCandidates()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.FindResults[(PremiereMediaType.Series, "tvdb_id", "9002")] = 102;
        tmdb.FindExceptions[(PremiereMediaType.Series, "tvdb_id", "9001")] =
            new OperationCanceledException("TMDb find request timed out.");
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "External IDs",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Timed Out Show",
                    null,
                    null,
                    9001,
                    "External IDs"),
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Resolved Show",
                    null,
                    null,
                    9002,
                    "External IDs")
            ]
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery],
            enrichmentProgressBatchSize: 1);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: NewSeriesOnlyFilters());

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item =>
            item.Title == "Resolved Show"
            && item.VerificationState == PremiereVerificationState.Verified);
        Assert.Contains(items, item =>
            item.Title == "Timed Out Show"
            && item.VerificationState == PremiereVerificationState.Unverified);
        Assert.DoesNotContain(reports, report => report.HasSourceErrors);
        Assert.Contains(tmdb.FindCalls, call => call.ExternalId == "9001");
        Assert.Contains(tmdb.FindCalls, call => call.ExternalId == "9002");
    }

    [Fact]
    public async Task GetPremieresAsync_DeduplicatesTmdbAndExternalDiscoveryRows()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 50,
                    Name = "Shared TMDb Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Shared External Show",
                    50,
                    null,
                    null,
                    "Fake")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("tv:50:air:20260504", item.CanonicalId);
        Assert.Equal(PremiereItemType.SeriesEpisode, item.Type);
        Assert.Equal("Shared TMDb Show", item.Title);
        Assert.Contains("Fake", item.SourceNames);
        Assert.Contains(item.Sources, source => source.Name == "Fake" && source.Kind == "schedule");
    }

    [Fact]
    public async Task GetPremieresAsync_DeduplicatesMovieRowsAndKeepsExternalSourceAttribution()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 60,
                    Title = "Shared TMDb Movie",
                    OriginalTitle = "Shared TMDb Movie",
                    ReleaseDate = "2026-05-04",
                    PrimaryReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Shared External Movie",
                    60,
                    null,
                    null,
                    "Fake")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("movie:60", item.CanonicalId);
        Assert.Equal("Shared TMDb Movie", item.Title);
        Assert.Contains("Fake", item.SourceNames);
        Assert.Contains(item.Sources, source => source.Name == "Fake" && source.Kind == "schedule");
    }

    [Fact]
    public async Task GetPremieresAsync_UnionsSourcesFromEquivalentExternalMovieCandidates()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 61,
                    Title = "Shared Provider Movie",
                    OriginalTitle = "Shared Provider Movie",
                    ReleaseDate = "2026-05-04",
                    PrimaryReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Watchmode releases",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 5),
                    "Shared Provider Movie",
                    61,
                    null,
                    null,
                    "Apple TV Store"),
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Shared Provider Movie",
                    61,
                    null,
                    null,
                    "Netflix")
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("movie:61", item.CanonicalId);
        Assert.Equal(new DateOnly(2026, 5, 4), item.PremiereDate);
        Assert.Contains("Apple TV Store", item.SourceNames);
        Assert.Contains("Netflix", item.SourceNames);
        Assert.Contains(item.Sources, source => source.Name == "Apple TV Store" && source.Kind == "schedule");
        Assert.Contains(item.Sources, source => source.Name == "Netflix" && source.Kind == "schedule");
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsSeriesOnlyDiscoveryProviderForMovieOnlyFilters()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 61,
                    Title = "Movie Only",
                    OriginalTitle = "Movie Only",
                    ReleaseDate = "2026-05-04",
                    PrimaryReleaseDate = "2026-05-04",
                    OriginalLanguage = "en"
                }
            ]
        };
        var tvmaze = new FakeTvmazeClient
        {
            ScheduleItems =
            [
                new TvmazeScheduleEpisode
                {
                    Airdate = "2026-05-04",
                    Show = new TvmazeShow
                    {
                        Id = 9001,
                        Name = "Series From Schedule",
                        Language = "English",
                        Externals = new TvmazeExternals { TheTvdb = 9001 }
                    }
                }
            ]
        };
        var tvmazeSchedules = new TvmazeScheduleDiscoveryProvider(
            tvmaze,
            Microsoft.Extensions.Options.Options.Create(new TvmazeOptions
            {
                Enabled = true,
                EnableScheduleDiscovery = true,
                ScheduleCountries = ["US"]
            }));
        var service = CreateService(
            tmdb,
            discoveryProviders: [tvmazeSchedules]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        Assert.Equal("Movie Only", Assert.Single(items).Title);
        Assert.Empty(tvmaze.ScheduleCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_RemovesGenericAirDateRowWhenExactEpisodeExistsForSameShowAndDay()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 51,
                    Name = "Shared Episode Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var discovery = new FakeDiscoveryProvider
        {
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Series,
                    new DateOnly(2026, 5, 4),
                    "Shared Episode Show",
                    51,
                    null,
                    null,
                    "Fake schedule",
                    IsSeriesEpisode: true,
                    EpisodeTitle: "Chapter One",
                    SeasonNumber: 1,
                    EpisodeNumber: 9)
            ]
        };
        var service = CreateService(
            tmdb,
            discoveryProviders: [discovery]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("tv:51:s01e09", item.CanonicalId);
        Assert.Equal(1, item.SeasonNumber);
        Assert.Equal(9, item.EpisodeNumber);
    }

    [Fact]
    public async Task GetPremieresAsync_CleansCachedGenericAirDateRowWhenExactEpisodeExistsForSameShowAndDay()
    {
        var cache = new FakeCalendarCache
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:52:air:20260504",
                    Type = PremiereItemType.SeriesEpisode,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 52,
                    Title = "Cached Episode Show",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    EpisodeSource = "TMDb air date"
                },
                new PremiereItem
                {
                    CanonicalId = "tv:52:s01e09",
                    Type = PremiereItemType.SeriesEpisode,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 52,
                    Title = "Cached Episode Show",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    EpisodeTitle = "Chapter One",
                    SeasonNumber = 1,
                    EpisodeNumber = 9,
                    EpisodeSource = "Fake schedule"
                }
            ]
        };
        var service = CreateService(new FakeTmdbClient(), calendarCache: cache);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("tv:52:s01e09", item.CanonicalId);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsFailedArtworkProviderWithoutBlankingWeek()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 60,
                    Name = "Resilient Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var service = CreateService(
            tmdb,
            artworkProviders: [new ThrowingArtworkProvider()]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("Resilient Show", item.Title);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsFailedOmdbRatingsWithoutDroppingItem()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 63,
                    Title = "Ratings Resilient Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.MovieDetailsById[63] = new TmdbDetailsWithExtras
        {
            Id = 63,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000063" }
        };
        var omdb = new FakeOmdbClient { ThrowOnGet = true };
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            omdb: omdb,
            calendarCache: cache);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal("Ratings Resilient Movie", item.Title);
        Assert.Null(item.ImdbScore);
        Assert.DoesNotContain(reports, report => report.HasSourceErrors);
        Assert.DoesNotContain(reports, report => report.ProgressText?.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotEmpty(cache.SetCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsFailedTvmazeEnrichmentWithoutDroppingSeries()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 64,
                    Name = "TVmaze Resilient Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.TvDetailsById[64] = new TmdbDetailsWithExtras
        {
            Id = 64,
            ExternalIds = new TmdbExternalIds { TvdbId = 6400 }
        };
        var tvmaze = new FakeTvmazeClient { ThrowOnLookup = true, ThrowOnSearch = true };
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(tmdb, tvmaze: tvmaze, calendarCache: cache);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("TVmaze Resilient Show", item.Title);
        Assert.Null(item.TvmazeUrl);
        Assert.DoesNotContain(reports, report => report.HasSourceErrors);
        Assert.DoesNotContain(reports, report => report.ProgressText?.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotEmpty(cache.SetCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_DoesNotCallExternalArtworkProvidersWhenTmdbPosterExists()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 70,
                    Name = "Poster Show",
                    FirstAirDate = "2026-05-04",
                    PosterPath = "/poster.jpg",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var artworkProvider = new RecordingArtworkProvider();
        var service = CreateService(
            tmdb,
            artworkProviders: [artworkProvider]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("TMDb poster", item.ImageSource);
        Assert.Empty(artworkProvider.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_UsesOmdbPosterBeforeExternalArtworkProviders()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 71,
                    Title = "OMDb Poster Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.MovieDetailsById[71] = new TmdbDetailsWithExtras
        {
            Id = 71,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000071" }
        };
        var omdb = new FakeOmdbClient
        {
            ItemsByImdbId =
            {
                ["tt0000071"] = new OmdbItem { Poster = "https://img.omdb.test/poster.jpg" }
            }
        };
        var artworkProvider = new RecordingArtworkProvider();
        var service = CreateService(
            tmdb,
            omdb: omdb,
            artworkProviders: [artworkProvider]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal("OMDb poster", item.ImageSource);
        Assert.Equal("https://img.omdb.test/poster.jpg", item.PosterUrl);
        Assert.Empty(artworkProvider.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_UsesTvmazeImageBeforeExternalArtworkProviders()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 72,
                    Name = "TVmaze Poster Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var tvmaze = new FakeTvmazeClient
        {
            TitleSearchResult = new TvmazeShow
            {
                Name = "TVmaze Poster Show",
                Image = new TvmazeImage { Original = "https://static.tvmaze.com/poster.jpg" }
            }
        };
        var artworkProvider = new RecordingArtworkProvider();
        var service = CreateService(
            tmdb,
            tvmaze: tvmaze,
            artworkProviders: [artworkProvider]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal(ArtworkSources.TvmazeImage, item.ImageSource);
        Assert.Equal("https://static.tvmaze.com/poster.jpg", item.PosterUrl);
        Assert.Empty(artworkProvider.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_KeepsExactTvmazeMetadataWhenTitleSearchOnlyAddsImage()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 73,
                    Name = "Exact TVmaze Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.TvDetailsById[73] = new TmdbDetailsWithExtras
        {
            Id = 73,
            ExternalIds = new TmdbExternalIds { TvdbId = 7300 }
        };
        var tvmaze = new FakeTvmazeClient
        {
            LookupResult = new TvmazeShow
            {
                Id = 7300,
                Name = "Exact TVmaze Show",
                Network = new TvmazeChannel { Name = "Exact Network" },
                AverageRuntime = 44
            },
            TitleSearchResult = new TvmazeShow
            {
                Id = 9999,
                Name = "Wrong TVmaze Show",
                Network = new TvmazeChannel { Name = "Wrong Network" },
                Image = new TvmazeImage { Original = "https://static.tvmaze.com/exact-poster.jpg" }
            }
        };
        var service = CreateService(tmdb, tvmaze: tvmaze);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("Exact Network", item.NetworkName);
        Assert.Equal(44, item.RuntimeMinutes);
        Assert.Equal("https://static.tvmaze.com/exact-poster.jpg", item.PosterUrl);
        Assert.Equal(ArtworkSources.TvmazeImage, item.ImageSource);
    }

    [Fact]
    public async Task GetPremieresAsync_StopsExternalArtworkLookupAfterFirstCandidate()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 74,
                    Name = "Fallback Poster Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var firstProvider = new RecordingArtworkProvider(new ArtworkCandidate("https://assets.fanart.tv/first.jpg", ArtworkSources.Fanart));
        var secondProvider = new RecordingArtworkProvider(new ArtworkCandidate("https://artworks.thetvdb.com/second.jpg", ArtworkSources.TheTvdb));
        var service = CreateService(
            tmdb,
            artworkProviders: [firstProvider, secondProvider]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal(ArtworkSources.Fanart, item.ImageSource);
        Assert.Single(firstProvider.Calls);
        Assert.Empty(secondProvider.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_ContinuesExternalArtworkLookupPastFailureAndNullCandidate()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 75,
                    Name = "Resilient Fallback Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var throwingProvider = new RecordingArtworkProvider(exception: new InvalidOperationException("Artwork outage."));
        var emptyProvider = new RecordingArtworkProvider(returnCandidate: false);
        var candidateProvider = new RecordingArtworkProvider(new ArtworkCandidate("https://assets.fanart.tv/recovered.jpg", ArtworkSources.Fanart));
        var laterProvider = new RecordingArtworkProvider(new ArtworkCandidate("https://artworks.thetvdb.com/later.jpg", ArtworkSources.TheTvdb));
        var service = CreateService(
            tmdb,
            artworkProviders: [throwingProvider, emptyProvider, candidateProvider, laterProvider]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal(ArtworkSources.Fanart, item.ImageSource);
        Assert.Single(throwingProvider.Calls);
        Assert.Single(emptyProvider.Calls);
        Assert.Single(candidateProvider.Calls);
        Assert.Empty(laterProvider.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_ContinuesExternalArtworkLookupPastProviderTimeout()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 76,
                    Name = "Timeout Fallback Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var timeoutProvider = new RecordingArtworkProvider(
            exception: new OperationCanceledException("Provider request timed out."));
        var candidateProvider = new RecordingArtworkProvider(
            new ArtworkCandidate("https://assets.fanart.tv/after-timeout.jpg", ArtworkSources.Fanart));
        var service = CreateService(
            tmdb,
            artworkProviders: [timeoutProvider, candidateProvider]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal(ArtworkSources.Fanart, item.ImageSource);
        Assert.Equal("https://assets.fanart.tv/after-timeout.jpg", item.PosterUrl);
        Assert.Single(timeoutProvider.Calls);
        Assert.Single(candidateProvider.Calls);
    }

    [Fact]
    public async Task GetPremieresAsync_UsesTmdbBackdropWhenNoPosterCandidateExists()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 77,
                    Name = "Backdrop Show",
                    FirstAirDate = "2026-05-04",
                    BackdropPath = "/backdrop.jpg",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        var service = CreateService(
            tmdb,
            artworkProviders: [new RecordingArtworkProvider(returnCandidate: false)]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Equal("TMDb backdrop", item.ImageSource);
        Assert.EndsWith("/w780/backdrop.jpg", item.PosterUrl);
    }

    [Fact]
    public async Task GetPremieresAsync_OrdersWatchProviderSourcesByConfiguredRegions()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 80,
                    Title = "Regional Providers",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.MovieDetailsById[80] = new TmdbDetailsWithExtras
        {
            Id = 80,
            WatchProviders = new TmdbWatchProviders
            {
                Results = new Dictionary<string, TmdbWatchProviderRegion>
                {
                    ["US"] = new TmdbWatchProviderRegion
                    {
                        Flatrate = [new TmdbWatchProvider { ProviderName = "US Stream", DisplayPriority = 1 }]
                    },
                    ["BE"] = new TmdbWatchProviderRegion
                    {
                        Flatrate = [new TmdbWatchProvider { ProviderName = "Belgian Stream", DisplayPriority = 1 }]
                    },
                    ["AU"] = new TmdbWatchProviderRegion
                    {
                        Flatrate = [new TmdbWatchProvider { ProviderName = "Australian Stream", DisplayPriority = 1 }]
                    },
                    ["GB"] = new TmdbWatchProviderRegion
                    {
                        Flatrate = [new TmdbWatchProvider { ProviderName = "UK Stream", DisplayPriority = 1 }]
                    }
                }
            }
        };
        var service = CreateService(tmdb, sourceRegions: ["BE", "US", "GB", "AU"]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(
            ["Belgian Stream", "US Stream", "UK Stream", "Australian Stream"],
            item.SourceNames);
    }

    [Fact]
    public async Task GetPremieresAsync_UsesWatchmodeSourcesWhenTmdbWatchProvidersAreMissing()
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems =
            [
                new TmdbTvDiscoverItem
                {
                    Id = 82,
                    Name = "Watchmode Show",
                    FirstAirDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.TvDetailsById[82] = new TmdbDetailsWithExtras
        {
            Id = 82,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000082" }
        };
        var watchmode = new FakeWatchmodeClient
        {
            Sources =
            [
                new PremiereSource { Name = "Watchmode Stream", Id = 501, Kind = "flatrate" }
            ]
        };
        var service = CreateService(
            tmdb,
            watchmode: watchmode,
            sourceRegions: ["BE", "NL"]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: NewSeriesOnlyFilters());

        var item = Assert.Single(items);
        Assert.Contains("Watchmode Stream", item.SourceNames);
        var call = Assert.Single(watchmode.SourceCalls);
        Assert.Equal(PremiereMediaType.Series, call.MediaType);
        Assert.Equal(82, call.TmdbId);
        Assert.Equal("tt0000082", call.ImdbId);
        Assert.Equal(["BE", "NL"], call.Regions);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsWatchmodeSourcesWhenTmdbWatchProvidersExist()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 83,
                    Title = "TMDb Provider Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.MovieDetailsById[83] = new TmdbDetailsWithExtras
        {
            Id = 83,
            WatchProviders = new TmdbWatchProviders
            {
                Results = new Dictionary<string, TmdbWatchProviderRegion>
                {
                    ["BE"] = new TmdbWatchProviderRegion
                    {
                        Flatrate = [new TmdbWatchProvider { ProviderName = "TMDb Stream", DisplayPriority = 1 }]
                    }
                }
            }
        };
        var watchmode = new FakeWatchmodeClient
        {
            Sources =
            [
                new PremiereSource { Name = "Watchmode Stream", Id = 501, Kind = "flatrate" }
            ]
        };
        var service = CreateService(tmdb, watchmode: watchmode, sourceRegions: ["BE"]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal(["TMDb Stream"], item.SourceNames);
        Assert.Empty(watchmode.SourceCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_DoesNotUseWatchmodeAvailabilityFallbackForExternalCandidates()
    {
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Trakt",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    new DateOnly(2026, 5, 4),
                    "Trakt Movie",
                    84,
                    null,
                    null,
                    "Trakt",
                    OriginalLanguage: "en")
            ]
        };
        var watchmode = new FakeWatchmodeClient
        {
            Sources =
            [
                new PremiereSource { Name = "Watchmode Stream", Id = 501, Kind = "flatrate" }
            ]
        };
        var service = CreateService(
            new FakeTmdbClient(),
            watchmode: watchmode,
            discoveryProviders: [discovery],
            sourceRegions: ["BE"]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal("Trakt Movie", item.Title);
        Assert.Contains("Trakt", item.SourceNames);
        Assert.Empty(watchmode.SourceCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsFailedWatchmodeAvailabilityWithoutDroppingItem()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 92,
                    Title = "Watchmode Resilient Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.MovieDetailsById[92] = new TmdbDetailsWithExtras { Id = 92 };
        var watchmode = new FakeWatchmodeClient { ThrowOnSources = true };
        var cache = new FakeCalendarCache();
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            watchmode: watchmode,
            calendarCache: cache,
            sourceRegions: ["US"]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal("Watchmode Resilient Movie", item.Title);
        Assert.DoesNotContain(reports, report => report.HasSourceErrors);
        Assert.DoesNotContain(
            reports,
            report => report.ProgressText?.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotEmpty(cache.SetCalls);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsTimedOutWatchmodeAvailabilityWithoutDroppingItem()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 93,
                    Title = "Watchmode Timeout Movie",
                    ReleaseDate = "2026-05-04",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ]
        };
        tmdb.MovieDetailsById[93] = new TmdbDetailsWithExtras { Id = 93 };
        var watchmode = new FakeWatchmodeClient
        {
            SourcesException = new OperationCanceledException("Watchmode request timed out.")
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(
            tmdb,
            watchmode: watchmode,
            sourceRegions: ["US"]);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal("Watchmode Timeout Movie", item.Title);
        Assert.DoesNotContain(reports, report => report.HasSourceErrors);
    }

    [Fact]
    public async Task GetPremieresAsync_SkipsTimedOutTmdbDetailEnrichmentWithoutDroppingItem()
    {
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 94,
                    Title = "Detail Timeout Movie",
                    ReleaseDate = "2026-05-04",
                    Overview = "Discover overview survives.",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"]
                }
            ],
            MovieDetailExceptions =
            {
                [94] = new OperationCanceledException("TMDb detail request timed out.")
            }
        };
        var reports = new List<PremiereLoadProgress>();
        var service = CreateService(tmdb);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None,
            progress: new RecordingProgress<PremiereLoadProgress>(reports.Add),
            filters: new CalendarFilters { ShowSeries = false, ShowMovies = true });

        var item = Assert.Single(items);
        Assert.Equal("Detail Timeout Movie", item.Title);
        Assert.Equal("Discover overview survives.", item.Overview);
        Assert.DoesNotContain(reports, report => report.HasSourceErrors);
    }

    [Fact]
    public async Task GetPremieresAsync_MergesDuplicateSourceAttributionFromCachedRows()
    {
        var cache = new FakeCalendarCache
        {
            Items =
            [
                new PremiereItem
                {
                    CanonicalId = "tv:42",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 42,
                    Title = "Shared Show",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    EpisodeSource = "TMDb air date",
                    SourceNames = ["TMDb"],
                    Sources =
                    [
                        new PremiereSource { Name = "TMDb", Kind = "calendar" }
                    ]
                },
                new PremiereItem
                {
                    CanonicalId = "tv:42",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 42,
                    Title = "Shared Show",
                    PremiereDate = new DateOnly(2026, 5, 4),
                    EpisodeSource = "TVmaze",
                    SourceNames = ["TVmaze"],
                    Sources =
                    [
                        new PremiereSource { Name = "TVmaze", Kind = "schedule" }
                    ]
                }
            ]
        };
        var service = CreateService(new FakeTmdbClient(), calendarCache: cache);

        var items = await service.GetPremieresAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var item = Assert.Single(items);
        Assert.Equal("tv:42", item.CanonicalId);
        Assert.Contains("TMDb", item.SourceNames);
        Assert.Contains("TVmaze", item.SourceNames);
        Assert.Contains(item.Sources, source => source.Name == "TMDb");
        Assert.Contains(item.Sources, source => source.Name == "TVmaze");
    }

    private static PremiereService CreateService(
        FakeTmdbClient tmdb,
        FakeTvmazeClient? tvmaze = null,
        IOmdbClient? omdb = null,
        IWatchmodeClient? watchmode = null,
        IEnumerable<IArtworkProvider>? artworkProviders = null,
        IEnumerable<IPremiereDiscoveryProvider>? discoveryProviders = null,
        ICalendarCache? calendarCache = null,
        string[]? sourceRegions = null,
        int sourceTimeoutSeconds = 120,
        int sourceFetchConcurrency = 4,
        int enrichmentProgressBatchSize = 10)
    {
        return new PremiereService(
            tmdb,
            omdb ?? new FakeOmdbClient(),
            tvmaze ?? new FakeTvmazeClient(),
            watchmode ?? new FakeWatchmodeClient(),
            calendarCache ?? new NullCalendarCache(),
            new TrailerSelector(),
            new RatingMapper(),
            artworkProviders ?? [],
            discoveryProviders ?? [],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions
            {
                SourceRegions = sourceRegions ?? [],
                SourceTimeoutSeconds = sourceTimeoutSeconds,
                SourceFetchConcurrency = sourceFetchConcurrency,
                EnrichmentProgressBatchSize = enrichmentProgressBatchSize
            }),
            NullLogger<PremiereService>.Instance);
    }

    private static string CacheKeyForPageMode(CalendarFilters filters, CalendarPageMode pageMode)
    {
        var pageFilters = CalendarFilterState.Clone(filters);
        CalendarFilterState.ApplyPageMode(pageFilters, pageMode);
        CalendarFilterState.Normalize(pageFilters);
        return PremiereDiscoveryCriteria.FromFilters(pageFilters).CacheKey();
    }

    private static CalendarFilters NewSeriesOnlyFilters()
    {
        return new CalendarFilters
        {
            SeriesFilters =
            {
                SeriesDateMode = SeriesDateMode.NewSeriesOnly
            }
        };
    }

    private sealed class FakeTmdbClient : ITmdbClient
    {
        public IReadOnlyList<TmdbTvDiscoverItem> TvItems { get; init; } = [];
        public Dictionary<string, IReadOnlyList<TmdbTvDiscoverItem>> TvItemsByOriginalLanguage { get; init; } = [];
        public IReadOnlyList<TmdbMovieDiscoverItem> MovieItems { get; init; } = [];
        public IReadOnlyList<TmdbDiscoverBatch<TmdbTvDiscoverItem>>? TvStreamBatches { get; init; }
        public TimeSpan? TvDetailDelay { get; init; }
        public TimeSpan? MovieDelay { get; init; }
        public IReadOnlyList<TmdbKeyword> KeywordResults { get; init; } = [];
        public Dictionary<int, TmdbDetailsWithExtras> TvDetailsById { get; } = [];
        public Dictionary<int, TmdbDetailsWithExtras> MovieDetailsById { get; } = [];
        public Dictionary<int, Exception> MovieDetailExceptions { get; init; } = [];
        public ConcurrentBag<DiscoverCall> TvCalls { get; } = [];
        public ConcurrentBag<NetworkDiscoverCall> TvNetworkCalls { get; } = [];
        public ConcurrentBag<DiscoverCall> MovieCalls { get; } = [];
        public IReadOnlyList<TmdbTitleSearchResult> MovieTitleSearchResults { get; init; } = [];
        public IReadOnlyList<TmdbTitleSearchResult> TvTitleSearchResults { get; init; } = [];
        public ConcurrentBag<TitleSearchCall> TitleSearchCalls { get; } = [];
        private int _activeMovieDiscoveries;
        private int _maxConcurrentMovieDiscoveries;
        public int MaxConcurrentMovieDiscoveries => _maxConcurrentMovieDiscoveries;
        public Dictionary<(PremiereMediaType MediaType, string Source, string ExternalId), int?> FindResults { get; } = [];
        public Dictionary<(PremiereMediaType MediaType, string Source, string ExternalId), Exception> FindExceptions { get; } = [];

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            TvCalls.Add(new DiscoverCall(filters, forceRefresh));
            var tvItems = !string.IsNullOrWhiteSpace(filters.OriginalLanguage)
                && TvItemsByOriginalLanguage.TryGetValue(filters.OriginalLanguage, out var languageItems)
                    ? languageItems
                    : TvItems;
            if (!filters.UseEpisodeAirDate)
            {
                return Task.FromResult(tvItems);
            }

            var airDate = start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult<IReadOnlyList<TmdbTvDiscoverItem>>(
                tvItems.Where(item => string.Equals(item.FirstAirDate, airDate, StringComparison.Ordinal)).ToArray());
        }

        public async IAsyncEnumerable<TmdbDiscoverBatch<TmdbTvDiscoverItem>> StreamDiscoverTvAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (TvStreamBatches is not null)
            {
                TvCalls.Add(new DiscoverCall(filters, forceRefresh));
                foreach (var batch in TvStreamBatches)
                {
                    yield return batch;
                    await Task.Yield();
                }

                yield break;
            }

            var items = await DiscoverTvAsync(start, end, filters, cancellationToken, forceRefresh);
            yield return new TmdbDiscoverBatch<TmdbTvDiscoverItem>(1, 1, 1, items.Count, items);
        }

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvByNetworksAsync(
            DateOnly start,
            DateOnly end,
            IReadOnlyList<int> networkIds,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            TvNetworkCalls.Add(new NetworkDiscoverCall(networkIds.ToArray(), forceRefresh));
            return Task.FromResult<IReadOnlyList<TmdbTvDiscoverItem>>([]);
        }

        public async Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            MovieCalls.Add(new DiscoverCall(filters, forceRefresh));
            var active = Interlocked.Increment(ref _activeMovieDiscoveries);
            var currentMax = Volatile.Read(ref _maxConcurrentMovieDiscoveries);
            while (active > currentMax)
            {
                var observed = Interlocked.CompareExchange(ref _maxConcurrentMovieDiscoveries, active, currentMax);
                if (observed == currentMax)
                {
                    break;
                }

                currentMax = observed;
            }

            if (MovieDelay is { } delay)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeMovieDiscoveries);
                }
            }
            else
            {
                Interlocked.Decrement(ref _activeMovieDiscoveries);
            }

            return MovieItems;
        }

        public async IAsyncEnumerable<TmdbDiscoverBatch<TmdbMovieDiscoverItem>> StreamDiscoverMoviesAsync(
            DateOnly start,
            DateOnly end,
            TmdbDiscoverFilters filters,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            var items = await DiscoverMoviesAsync(start, end, filters, cancellationToken, forceRefresh);
            yield return new TmdbDiscoverBatch<TmdbMovieDiscoverItem>(1, 1, 1, items.Count, items);
        }

        public ConcurrentBag<DetailCall> TvDetailCalls { get; } = [];

        public Task<TmdbDetailsWithExtras?> GetTvDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            TvDetailCalls.Add(new DetailCall(id, forceRefresh));
            if (TvDetailDelay is { } delay)
            {
                return DelayTvDetailsAsync(id, delay, cancellationToken);
            }

            return Task.FromResult(CreateTvDetails(id));
        }

        private TmdbDetailsWithExtras? CreateTvDetails(int id)
        {
            if (TvDetailsById.TryGetValue(id, out var details))
            {
                return details;
            }

            return new TmdbDetailsWithExtras
            {
                Id = id,
                Videos = new TmdbVideoResponse
                {
                    Results =
                    [
                        new TmdbVideo
                        {
                            Site = "YouTube",
                            Type = "Trailer",
                            Key = "series-trailer",
                            Official = true
                        }
                    ]
                }
            };
        }

        private async Task<TmdbDetailsWithExtras?> DelayTvDetailsAsync(int id, TimeSpan delay, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return CreateTvDetails(id);
        }

        public Task<TmdbDetailsWithExtras?> GetMovieDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            if (MovieDetailExceptions.TryGetValue(id, out var exception))
            {
                throw exception;
            }

            return Task.FromResult(MovieDetailsById.TryGetValue(id, out var details) ? details : null);
        }

        public Task<int?> FindTmdbIdByExternalIdAsync(
            PremiereMediaType mediaType,
            string externalId,
            string externalSource,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            FindCalls.Add(new FindCall(mediaType, externalId, externalSource, forceRefresh));
            if (FindExceptions.TryGetValue((mediaType, externalSource, externalId), out var exception))
            {
                throw exception;
            }

            return Task.FromResult(FindResults.GetValueOrDefault((mediaType, externalSource, externalId)));
        }

        public Task<IReadOnlyList<TmdbTitleSearchResult>> SearchTitlesAsync(
            PremiereMediaType mediaType,
            string query,
            int? year,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            TitleSearchCalls.Add(new TitleSearchCall(mediaType, query, year, forceRefresh));
            return Task.FromResult(mediaType == PremiereMediaType.Movie
                ? MovieTitleSearchResults
                : TvTitleSearchResults);
        }

        public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(PremiereMediaType mediaType, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbGenre>>([]);
        }

        public Task<IReadOnlyList<TmdbConfigurationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbConfigurationLanguage>>([]);
        }

        public Task<IReadOnlyList<TmdbConfigurationCountry>> GetCountriesAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbConfigurationCountry>>([]);
        }

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
            PremiereMediaType mediaType,
            string region,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
        }

        public Task<TmdbCertificationResponse?> GetCertificationsAsync(
            PremiereMediaType mediaType,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<TmdbCertificationResponse?>(null);
        }

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(
            string query,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult(KeywordResults);
        }

        public ConcurrentBag<FindCall> FindCalls { get; } = [];
    }

    private sealed class FakeOmdbClient : IOmdbClient
    {
        public ConcurrentBag<string> Calls { get; } = [];
        public Dictionary<string, OmdbItem> ItemsByImdbId { get; } = [];
        public bool ThrowOnGet { get; init; }

        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls.Add(imdbId);
            if (ThrowOnGet)
            {
                throw new ExternalApiException("OMDb failed.");
            }

            return Task.FromResult(ItemsByImdbId.GetValueOrDefault(imdbId));
        }
    }

    private sealed class FakeTvmazeClient : ITvmazeClient
    {
        public TvmazeShow? TitleSearchResult { get; init; }
        public TvmazeShow? LookupResult { get; init; }
        public IReadOnlyList<TvmazeScheduleEpisode> ScheduleItems { get; init; } = [];
        public ConcurrentBag<TvmazeSearchCall> SearchCalls { get; } = [];
        public ConcurrentBag<TvmazeScheduleCall> ScheduleCalls { get; } = [];
        public bool ThrowOnLookup { get; init; }
        public bool ThrowOnSearch { get; init; }

        public Task<TvmazeShow?> LookupShowAsync(int? tvdbId, string? imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            if (ThrowOnLookup)
            {
                throw new ExternalApiException("TVmaze lookup failed.");
            }

            return Task.FromResult(LookupResult);
        }

        public Task<TvmazeShow?> SearchShowByNameAsync(string title, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            SearchCalls.Add(new TvmazeSearchCall(title, forceRefresh));
            if (ThrowOnSearch)
            {
                throw new ExternalApiException("TVmaze search failed.");
            }

            return Task.FromResult(TitleSearchResult);
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
            ScheduleCalls.Add(new TvmazeScheduleCall(date, country, webSchedule, forceRefresh));
            return Task.FromResult(ScheduleItems);
        }
    }

    private sealed class NullCalendarCache : ICalendarCache
    {
        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(
            DateOnly start,
            DateOnly end,
            string cacheKey,
            CancellationToken cancellationToken,
            bool allowExpired = false)
        {
            return Task.FromResult<IReadOnlyList<PremiereItem>?>(null);
        }

        public Task SetWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCalendarCache : ICalendarCache
    {
        public IReadOnlyList<PremiereItem>? Items { get; init; }
        public IReadOnlyList<PremiereItem>? ExpiredItems { get; init; }
        public Dictionary<(DateOnly Start, DateOnly End, string CacheKey), IReadOnlyList<PremiereItem>> ItemsByKey { get; } = [];
        public Dictionary<(DateOnly Start, DateOnly End, string CacheKey), IReadOnlyList<PremiereItem>> ExpiredItemsByKey { get; } = [];
        public List<CacheSetCall> SetCalls { get; } = [];

        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(
            DateOnly start,
            DateOnly end,
            string cacheKey,
            CancellationToken cancellationToken,
            bool allowExpired = false)
        {
            var key = (start, end, cacheKey);
            if (allowExpired)
            {
                if (ExpiredItemsByKey.TryGetValue(key, out var expiredItems))
                {
                    return Task.FromResult<IReadOnlyList<PremiereItem>?>(expiredItems);
                }

                if (ExpiredItems is not null)
                {
                    return Task.FromResult<IReadOnlyList<PremiereItem>?>(ExpiredItems);
                }
            }

            return Task.FromResult(
                ItemsByKey.TryGetValue(key, out var items)
                    ? items
                    : Items);
        }

        public Task SetWeekAsync(
            DateOnly start,
            DateOnly end,
            string cacheKey,
            IReadOnlyList<PremiereItem> items,
            CancellationToken cancellationToken)
        {
            var stored = items.ToArray();
            SetCalls.Add(new CacheSetCall(start, end, cacheKey, stored));
            ItemsByKey[(start, end, cacheKey)] = stored;
            return Task.CompletedTask;
        }
    }

    private sealed record CacheSetCall(
        DateOnly Start,
        DateOnly End,
        string CacheKey,
        IReadOnlyList<PremiereItem> Items);

    public sealed record DiscoverCall(TmdbDiscoverFilters Filters, bool ForceRefresh);

    public sealed record NetworkDiscoverCall(IReadOnlyList<int> NetworkIds, bool ForceRefresh);

    public sealed record DetailCall(int Id, bool ForceRefresh);

    public sealed record TvmazeSearchCall(string Title, bool ForceRefresh);

    public sealed record TvmazeScheduleCall(DateOnly Date, string? Country, bool WebSchedule, bool ForceRefresh);

    public sealed record FindCall(PremiereMediaType MediaType, string ExternalId, string ExternalSource, bool ForceRefresh);

    public sealed record TitleSearchCall(PremiereMediaType MediaType, string Query, int? Year, bool ForceRefresh);

    public sealed record WatchmodeSourceCall(
        PremiereMediaType MediaType,
        int TmdbId,
        string? ImdbId,
        string[] Regions,
        bool ForceRefresh);

    private sealed class FakeWatchmodeClient : IWatchmodeClient
    {
        public IReadOnlyList<PremiereSource> Sources { get; init; } = [];
        public IReadOnlyList<ExternalPremiereCandidate> ReleaseCandidates { get; init; } = [];
        public bool ThrowOnSources { get; init; }
        public Exception? SourcesException { get; init; }
        public ConcurrentBag<WatchmodeSourceCall> SourceCalls { get; } = [];

        public Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(
            PremiereMediaType mediaType,
            int tmdbId,
            string? imdbId,
            IReadOnlyList<string> regions,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            SourceCalls.Add(new WatchmodeSourceCall(mediaType, tmdbId, imdbId, regions.ToArray(), forceRefresh));
            if (ThrowOnSources)
            {
                throw new ExternalApiException("Watchmode sources failed.");
            }

            if (SourcesException is not null)
            {
                throw SourcesException;
            }

            return Task.FromResult(Sources);
        }

        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult(ReleaseCandidates);
        }
    }

    private sealed class FakeDiscoveryProvider : INamedPremiereDiscoveryProvider
    {
        public string DisplayName { get; init; } = "Fake discovery";

        public IReadOnlyList<ExternalPremiereCandidate> Candidates { get; init; } = [];
        public TimeSpan Delay { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            Started.TrySetResult();
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return Candidates;
        }
    }

    private sealed class ThrowingArtworkProvider : IArtworkProvider
    {
        public Task<ArtworkCandidate?> GetArtworkAsync(
            ArtworkRequest request,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            throw new InvalidOperationException("Simulated artwork outage.");
        }
    }

    private sealed class RecordingArtworkProvider : IArtworkProvider
    {
        private readonly ArtworkCandidate? _candidate;
        private readonly Exception? _exception;

        public RecordingArtworkProvider(
            ArtworkCandidate? candidate = null,
            Exception? exception = null,
            bool returnCandidate = true)
        {
            _candidate = returnCandidate
                ? candidate ?? new ArtworkCandidate("https://assets.fanart.tv/poster.jpg", ArtworkSources.Fanart)
                : null;
            _exception = exception;
        }

        public ConcurrentBag<ArtworkRequest> Calls { get; } = [];

        public Task<ArtworkCandidate?> GetArtworkAsync(
            ArtworkRequest request,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            Calls.Add(request);
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_candidate);
        }
    }

    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }
}
