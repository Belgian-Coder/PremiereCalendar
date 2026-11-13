using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace PremiereCalendar.Services;

/// <summary>A provider outcome, separate from verified title absence and safe for diagnostics.</summary>
public sealed record ProviderFailure(string Reason, int? StatusCode = null, DateTimeOffset? RetryAtUtc = null, bool IsLocal = false)
{
    public static ProviderFailure FromException(Exception exception) => exception switch
    {
        ProviderRequestException request => request.Failure,
        ProviderRetryAfterException { ResponseObserved: true } retry => new(
            retry.StatusCode == HttpStatusCode.TooManyRequests ? "rate_limited" : "http_failure",
            (int?)retry.StatusCode, retry.RetryAtUtc),
        ProviderRetryAfterException retry => new("local_backoff", 429, retry.RetryAtUtc, true),
        ProviderCircuitOpenException circuit => new("local_circuit", (int?)circuit.StatusCode, circuit.RetryAtUtc, true),
        OperationCanceledException or TimeoutException => new("timeout"),
        HttpRequestException http => new("http_failure", (int?)http.StatusCode),
        _ => new("invalid_response")
    };

    internal static ProviderFailure FromResponse(HttpResponseMessage response, DateTimeOffset now, TimeSpan? defaultRateDelay = null)
    {
        var status = (int)response.StatusCode;
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_denied",
            HttpStatusCode.TooManyRequests => "rate_limited",
            _ => "http_failure"
        };
        var retry = response.Headers.RetryAfter?.Date
            ?? (response.Headers.RetryAfter?.Delta is { } delay ? now.Add(delay) : (DateTimeOffset?)null);
        if (reason == "authentication_denied") retry ??= now.AddMinutes(15);
        if (reason == "rate_limited") retry ??= now.Add(defaultRateDelay ?? TimeSpan.FromMinutes(1));
        return new(reason, status, retry);
    }
}

public sealed class ProviderRequestException(string provider, ProviderFailure failure)
    : HttpRequestException($"{provider} provider failed: {failure.Reason}.", null,
        failure.StatusCode is { } status ? (HttpStatusCode)status : null)
{
    public ProviderFailure Failure { get; } = failure;
}

// The adaptive HTTP policy persists provider circuits. This cache also protects direct
// clients and payload-level denials; credentials are hashed and never logged.
internal sealed class ProviderFailureCache(IMemoryCache cache, string provider, Uri? endpoint, string credential = "")
{
    private readonly string _key = "provider:failure:" + provider + ":" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint?.GetLeftPart(UriPartial.Authority)}:{credential}")));

    internal ProviderFailure? Get(DateTimeOffset now)
        => cache.TryGetValue(_key, out ProviderFailure? failure) && failure?.RetryAtUtc > now
            ? failure with { Reason = "local_backoff", IsLocal = true } : null;

    internal void Clear() => cache.Remove(_key);

    internal void Remember(ProviderFailure failure, DateTimeOffset now)
    {
        if (failure.RetryAtUtc is not { } until || until <= now) return;
        if (cache.TryGetValue(_key, out ProviderFailure? existing) && existing?.RetryAtUtc > until) return;
        cache.Set(_key, failure, until - now);
    }
}

internal static class ProviderOutcomeDiagnostics
{
    internal static void Record(PremiereTelemetry? telemetry, string provider, string outcome, string reason, bool fromCache = false)
        => telemetry?.RecordDiagnostic("provider.outcome", telemetry.CaptureDiagnosticContext(), new()
        {
            Provider = provider, Outcome = outcome, Reason = reason,
            FromCache = fromCache, Cache = fromCache ? "hit" : null
        });
}

internal static class ProviderRequestOptions
{
    internal static readonly HttpRequestOptionsKey<bool> AllowAuthenticationRefresh = new("PremiereCalendar.AllowAuthenticationRefresh");
    /// <summary>Decoded response-size limit in bytes; absent means unlimited (images and IMDb dataset downloads).</summary>
    internal static readonly HttpRequestOptionsKey<long> MaxResponseBytes = new("PremiereCalendar.MaxResponseBytes");
}
