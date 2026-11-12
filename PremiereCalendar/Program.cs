using System.Globalization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using System.Text.Json;
using PremiereCalendar.Components;
using PremiereCalendar.Hosting;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

var builder = WebApplication.CreateBuilder(args);

if (DatabaseCommandLine.IsDatabaseCommand(args))
{
    Environment.ExitCode = await DatabaseCommandLine.RunAsync(args, builder.Configuration, builder.Environment);
    return;
}

builder.Services.AddHostingHardening(builder.Configuration);
builder.AddPremiereTelemetry();

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "PremiereCalendar";
});
builder.Services.Configure<HostOptions>(options =>
{
    // Keep signed-update restarts bounded even when a provider request is slow to observe cancellation.
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
});
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5298");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
        options.DisconnectedCircuitMaxRetained = 200;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(60);
        options.DetailedErrors = builder.Environment.IsDevelopment();
    })
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        options.MaximumReceiveMessageSize = 1024 * 1024;
    });
builder.Services.Configure<CircuitOptions>(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    options.DisconnectedCircuitMaxRetained = 200;
    options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(60);
    options.DetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddPremiereOptions(builder.Configuration);

builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/manifest+json",
        "image/svg+xml"
    ]);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks()
    .AddCheck<SqliteHealthCheck>("sqlite", tags: ["ready"]);
builder.Services.AddSingleton<DatabaseRecoveryState>();
builder.Services.AddSingleton<SqliteDatabaseInitializer>();
builder.Services.AddSingleton<PostgresDatabaseInitializer>();
// Register first so WAL and busy-timeout settings are applied before cache warmers start.
builder.Services.AddHostedService<DatabaseInitializerHostedService>();
builder.Services.AddSingleton<CircuitHandler, CalendarCircuitDiagnostics>();

builder.Services
    .AddPremierePersistence()
    .AddPremiereScheduling()
    .AddPremiereProviderClients()
    .AddPremiereCalendarServices(builder.Configuration);

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() =>
    app.Services.GetRequiredService<PremiereTelemetry>().RecordDiagnostic("diagnostics.started",
        details: new() { Stage = "startup", Outcome = "running" }));
app.Services.GetRequiredService<PremiereTelemetry>().RecordVersionValidation(
    "database_schema",
    BuildVersionInfo.Current.DatabaseSchemaVersion == DatabaseSchema.CurrentVersion);

// Forwarded headers must be applied before HTTPS redirection and HSTS determine the scheme.
app.UseHostingHardening();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseResponseCompression();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets()
    .ShortCircuit();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapGet("/health/version", (DatabaseRecoveryState databaseState, IOptions<AppDatabaseOptions> databaseOptions, PremiereTelemetry telemetry) => Results.Json(new
{
    version = BuildVersionInfo.Current.Version,
    informationalVersion = BuildVersionInfo.Current.InformationalVersion,
    sourceRevision = BuildVersionInfo.Current.SourceRevision,
    buildId = BuildVersionInfo.Current.BuildId,
    buildTimeUtc = BuildVersionInfo.Current.BuildTimeUtc,
    databaseSchemaVersion = BuildVersionInfo.Current.DatabaseSchemaVersion,
    performanceDiagnostics = telemetry.DiagnosticsHealth,
    operationalLogging = OperationalLoggingDiagnostics.Snapshot,
    database = new
    {
        currentSchemaVersion = databaseState.Snapshot.CurrentVersion,
        targetSchemaVersion = databaseState.Snapshot.TargetVersion,
        healthy = databaseState.Snapshot.IsHealthy,
        integrity = databaseState.Snapshot.Message,
        lastMigration = databaseState.Snapshot.LastMigration,
        recovery = DatabaseConnectionFactory.IsPostgreSql(databaseOptions.Value)
            ? "Run 'dotnet PremiereCalendar.dll database verify'. Restore only a checksum-verified pg_dump into an isolated PostgreSQL instance before production recovery."
            : "Stop the Windows Service, run 'PremiereCalendar.exe database verify', then use 'database restore --backup <absolute-path>' only with a verified backup."
    }
}));
app.MapGet(
    "/cached-image",
    async Task<IResult> (
        string url,
        int? w,
        IImageCache imageCache,
        IOptions<ImageCacheOptions> options,
        HttpContext httpContext,
        ILogger<Program> logger,
        CancellationToken cancellationToken) =>
    {
        if (!options.Value.Enabled)
        {
            return ImageSourceUrlPolicy.TryCreateAllowedUri(url, options.Value.AllowedHosts, out var sourceUri)
                ? Results.Redirect(sourceUri.AbsoluteUri)
                : Results.BadRequest();
        }

        // Image bodies must never be interpreted as anything but an inert image, even if a provider served markup.
        httpContext.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        httpContext.Response.Headers.XContentTypeOptions = "nosniff";

        try
        {
            var width = CachedImageUrl.SnapWidth(w);
            var format = width is > 0
                && httpContext.Request.GetTypedHeaders().Accept?.Any(mediaType =>
                    string.Equals(mediaType.MediaType.Value, "image/webp", StringComparison.OrdinalIgnoreCase)) == true
                    ? ImageCacheFormat.Webp
                    : ImageCacheFormat.Original;
            // Forced refresh is internal-only (background refresh queue); it is not reachable from the public query string.
            var image = await imageCache.GetOrAddAsync(url, false, cancellationToken, width, format);
            if (!CachedImageUrl.TryGetRasterContentType(image.ContentType, out var contentType))
            {
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            }

            var maxAgeSeconds = Math.Max(60, Convert.ToInt32(image.BrowserMaxAge.TotalSeconds));
            var entityTag = $"\"{image.ContentHash ?? image.CacheKey}\"";

            httpContext.Response.Headers.CacheControl = $"public, max-age={maxAgeSeconds}";
            httpContext.Response.Headers.Vary = "Accept";
            httpContext.Response.Headers.ETag = entityTag;
            httpContext.Response.Headers.LastModified = image.LastModifiedUtc.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

            if (RequestHasMatchingEntityTag(httpContext, entityTag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(
                new FileStream(
                    image.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan),
                contentType,
                lastModified: image.LastModifiedUtc,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(entityTag),
                enableRangeProcessing: true);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest();
        }
        catch (ExternalApiException)
        {
            return Results.NotFound();
        }
          catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or UnauthorizedAccessException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep unexpected failures empty-bodied and header-protected instead of falling into the HTML error page.
            logger.LogError(ex, "Unexpected failure while serving a cached image.");
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    })
    // Failed image requests must stay empty-bodied instead of re-executing the HTML not-found page.
    .WithMetadata(new SkipStatusCodePagesMetadata());

try
{
    app.Run();
}
catch (OperationCanceledException)
{
    // WindowsServiceLifetime can surface its canceled stop token after the application-stopping
    // signal has already completed. A canceled host run is a normal service shutdown, not a crash.
}

static bool RequestHasMatchingEntityTag(HttpContext httpContext, string entityTag)
{
    if (!httpContext.Request.Headers.TryGetValue("If-None-Match", out var values))
    {
        return false;
    }

    return values
        .SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(value => value == "*" || string.Equals(value, entityTag, StringComparison.Ordinal));
}

public partial class Program;

/// <summary>Marks an endpoint whose error responses must not be re-executed as HTML status pages.</summary>
internal sealed class SkipStatusCodePagesMetadata : Microsoft.AspNetCore.Http.Metadata.ISkipStatusCodePagesMetadata;

