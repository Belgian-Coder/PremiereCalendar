using System.Runtime.CompilerServices;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class DiagnosticIdentifierTests
{
    // Reference implementations: the LINQ/Guid based originals the optimized code must match exactly.
    private static string? ReferenceCode(string? value)
        => value is null ? null : value.Length is > 0 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? value : "redacted";

    private static string? ReferenceCorrelation(string? value)
        => value is null ? null : Guid.TryParse(value, out var id) ? id.ToString("N") : "redacted";

    private static string? ReferenceCacheKey(string? value)
        => value is null ? null : value == "default" || (value.Length is 20 or 64 && value.All(Uri.IsHexDigit))
            ? value : "redacted";

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "redacted")]
    [InlineData("a", "a")]
    [InlineData("provider.lookup_completed-1", "provider.lookup_completed-1")]
    [InlineData("has space", "redacted")]
    [InlineData("https://host/path", "redacted")]
    [InlineData("café", "redacted")]
    [InlineData("١٢", "redacted")]
    [InlineData("tab\t", "redacted")]
    public void CodeAcceptsOnlyAsciiIdentifiers(string? value, string? expected)
    {
        Assert.Equal(expected, DiagnosticIdentifier.Code(value));
        Assert.Equal(ReferenceCode(value), DiagnosticIdentifier.Code(value));
    }

    [Fact]
    public void CodeEnforcesMaximumLengthAndReturnsTheSameInstanceWhenAccepted()
    {
        var max = new string('a', 64);
        var tooLong = new string('a', 65);
        Assert.Same(max, DiagnosticIdentifier.Code(max));
        Assert.Equal("redacted", DiagnosticIdentifier.Code(tooLong));
        Assert.Equal("redacted", DiagnosticIdentifier.Code(new string('a', 63) + "!"));
        Assert.Equal("redacted", DiagnosticIdentifier.Code(new string('a', 64) + "é"));
    }

    [Fact]
    public void CodeMatchesReferenceForEveryCharacterBoundary()
    {
        for (var code = 0; code < 0x250; code++)
        {
            var text = "x" + (char)code + "y";
            Assert.Equal(ReferenceCode(text), DiagnosticIdentifier.Code(text));
            Assert.Equal(ReferenceCode(((char)code).ToString()), DiagnosticIdentifier.Code(((char)code).ToString()));
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "redacted")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", "0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789AbCdEf0123456789aBcDeF", "0123456789abcdef0123456789abcdef")]
    [InlineData("01234567-89ab-cdef-0123-456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("{01234567-89AB-CDEF-0123-456789ABCDEF}", "0123456789abcdef0123456789abcdef")]
    [InlineData("(01234567-89ab-cdef-0123-456789abcdef)", "0123456789abcdef0123456789abcdef")]
    [InlineData("  0123456789abcdef0123456789abcdef  ", "0123456789abcdef0123456789abcdef")]
    [InlineData("00000000000000000000000000000000", "00000000000000000000000000000000")]
    [InlineData("0123456789abcdef0123456789abcde", "redacted")]
    [InlineData("0123456789abcdef0123456789abcdef0", "redacted")]
    [InlineData("0123456789abcdef0123456789abcdeg", "redacted")]
    [InlineData("0123456789abcdef0123456789abcde١", "redacted")]
    [InlineData("not-a-guid", "redacted")]
    public void CorrelationNormalizesToLowercaseNFormOrRedacts(string? value, string? expected)
    {
        Assert.Equal(expected, DiagnosticIdentifier.Correlation(value));
        Assert.Equal(ReferenceCorrelation(value), DiagnosticIdentifier.Correlation(value));
    }

    [Fact]
    public void CorrelationOfAlreadyNormalizedIdReturnsTheSameInstance()
    {
        var id = Guid.NewGuid().ToString("N");
        Assert.Same(id, DiagnosticIdentifier.Correlation(id));
    }

    [Fact]
    public void CorrelationMatchesReferenceForRandomGuidsInEveryFormat()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = Guid.NewGuid();
            foreach (var format in new[] { "N", "D", "B", "P", "X" })
            {
                foreach (var text in new[] { id.ToString(format), id.ToString(format).ToUpperInvariant() })
                    Assert.Equal(ReferenceCorrelation(text), DiagnosticIdentifier.Correlation(text));
            }
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "redacted")]
    [InlineData("default", "default")]
    [InlineData("DEFAULT", "redacted")]
    [InlineData("0123456789abcdef0123", "0123456789abcdef0123")]
    [InlineData("0123456789ABCDEF0123", "0123456789ABCDEF0123")]
    [InlineData("0123456789abcdef012", "redacted")]
    [InlineData("0123456789abcdef01234", "redacted")]
    [InlineData("0123456789abcdef012g", "redacted")]
    [InlineData("0123456789abcdef١123", "redacted")]
    public void CacheKeyAcceptsDefaultOrHexOfFixedLengths(string? value, string? expected)
    {
        Assert.Equal(expected, DiagnosticIdentifier.CacheKey(value));
        Assert.Equal(ReferenceCacheKey(value), DiagnosticIdentifier.CacheKey(value));
    }

    [Fact]
    public void CacheKeyAcceptsSixtyFourHexAndRejectsSixtyThreeAndSixtyFive()
    {
        var hex64 = new string('a', 32) + new string('F', 32);
        Assert.Equal(hex64, DiagnosticIdentifier.CacheKey(hex64));
        Assert.Equal("redacted", DiagnosticIdentifier.CacheKey(hex64[..63]));
        Assert.Equal("redacted", DiagnosticIdentifier.CacheKey(hex64 + "a"));
    }

    [Fact]
    public void SanitizeReturnsSameInstanceWhenNothingChangesAndSanitizesOtherwise()
    {
        var clean = new PerformanceDiagnosticDetails
        {
            Stage = "load", Provider = "tmdb", Outcome = "ok", Reason = "none", Cache = "hit", JobKind = "warmup",
            SourceKey = "tmdb.movies", MediaType = "movie", CacheKey = "default", DurationMilliseconds = 1.5,
            WaitMilliseconds = 0, SerializeMilliseconds = null, EncodeMilliseconds = 2, StoreMilliseconds = 3,
            Count = 4, FromCache = true
        };
        Assert.Same(clean, clean.Sanitize());
        var empty = new PerformanceDiagnosticDetails();
        Assert.Same(empty, empty.Sanitize());

        var dirty = clean with
        {
            Stage = "bad stage", CacheKey = "nothex", DurationMilliseconds = double.NaN,
            WaitMilliseconds = -3, SerializeMilliseconds = double.PositiveInfinity, EncodeMilliseconds = -0.0
        };
        var sanitized = dirty.Sanitize();
        Assert.NotSame(dirty, sanitized);
        Assert.Equal("redacted", sanitized.Stage);
        Assert.Equal("redacted", sanitized.CacheKey);
        Assert.Null(sanitized.DurationMilliseconds);
        Assert.Equal(0d, sanitized.WaitMilliseconds);
        Assert.Null(sanitized.SerializeMilliseconds);
        Assert.False(double.IsNegative(sanitized.EncodeMilliseconds!.Value));
        Assert.Equal("tmdb", sanitized.Provider);
        Assert.Equal(4, sanitized.Count);
        Assert.True(sanitized.FromCache);
    }

    [Fact]
    public void SanitizeNormalizesEachStringFieldIndependently()
    {
        var fields = new Func<string, PerformanceDiagnosticDetails>[]
        {
            value => new() { Stage = value }, value => new() { Provider = value }, value => new() { Outcome = value },
            value => new() { Reason = value }, value => new() { Cache = value }, value => new() { JobKind = value },
            value => new() { SourceKey = value }, value => new() { MediaType = value },
        };
        foreach (var make in fields)
        {
            var sanitized = make("https://secret.example/?q=1").Sanitize();
            Assert.DoesNotContain("secret", string.Join('|', sanitized.Stage, sanitized.Provider, sanitized.Outcome,
                sanitized.Reason, sanitized.Cache, sanitized.JobKind, sanitized.SourceKey, sanitized.MediaType));
        }
    }

    [Fact]
    public async Task AsyncLocalContextIsStableForProducerAndConsumerAcrossManyAwaits()
    {
        var telemetry = new PremiereTelemetry();
        var consumer = telemetry.CreateDiagnosticContext();
        var producer = telemetry.CreateDiagnosticContext();
        var seen = new List<(PerformanceDiagnosticContext? Producer, PerformanceDiagnosticContext? Consumer)>();
        var producerSeen = new Dictionary<int, PerformanceDiagnosticContext?>();

        using (telemetry.UseDiagnosticContext(consumer))
        {
            await foreach (var value in telemetry.WithDiagnosticContext(Produce(), producer))
            {
                Assert.Equal(consumer, telemetry.CaptureDiagnosticContext());
                await Task.Yield();
                Assert.Equal(consumer, telemetry.CaptureDiagnosticContext());
                await Task.Delay(1);
                seen.Add((producerSeen[value], telemetry.CaptureDiagnosticContext()));
            }
            Assert.Equal(consumer, telemetry.CaptureDiagnosticContext());
        }

        Assert.Null(telemetry.CaptureDiagnosticContext());
        Assert.Equal(3, seen.Count);
        Assert.All(seen, pair =>
        {
            Assert.Equal(producer, pair.Producer);
            Assert.Equal(consumer, pair.Consumer);
        });
        Assert.Equal(producer, producerSeen[-1]);

        // Producer-side observations are recorded in a dictionary because the iterator body runs under the
        // producer context and the consumer must not observe it.
        async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    await Task.Yield();
                    await Task.Delay(1, cancellationToken);
                    producerSeen[i] = telemetry.CaptureDiagnosticContext();
                    yield return i;
                }
            }
            finally
            {
                producerSeen[-1] = telemetry.CaptureDiagnosticContext();
            }
        }
    }

    [Fact]
    public async Task AsyncLocalContextDoesNotLeakAcrossSiblingConsumersOnDifferentFlows()
    {
        var telemetry = new PremiereTelemetry();
        var contexts = Enumerable.Range(0, 4).Select(_ => telemetry.CreateDiagnosticContext()).ToArray();
        await Task.WhenAll(contexts.Select(async context =>
        {
            var outer = telemetry.CreateDiagnosticContext();
            using (telemetry.UseDiagnosticContext(outer))
            {
                await foreach (var value in telemetry.WithDiagnosticContext(Produce(context), context))
                {
                    Assert.Equal(outer, telemetry.CaptureDiagnosticContext());
                    Assert.Equal(context.LoadId, value);
                }
                Assert.Equal(outer, telemetry.CaptureDiagnosticContext());
            }
        }));

        async IAsyncEnumerable<string?> Produce(PerformanceDiagnosticContext expected)
        {
            for (var i = 0; i < 5; i++)
            {
                await Task.Yield();
                Assert.Equal(expected, telemetry.CaptureDiagnosticContext());
                yield return telemetry.CaptureDiagnosticContext()?.LoadId;
            }
        }
    }
}
