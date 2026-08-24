using System.Globalization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
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
app.MapGet("/health/version", (DatabaseRecoveryState databaseState, IOptions<AppDatabaseOptions> databaseOptions) => Results.Json(new
{
    version = BuildVersionInfo.Current.Version,
    informationalVersion = BuildVersionInfo.Current.InformationalVersion,
    sourceRevision = BuildVersionInfo.Current.SourceRevision,
    buildId = BuildVersionInfo.Current.BuildId,
    buildTimeUtc = BuildVersionInfo.Current.BuildTimeUtc,
    databaseSchemaVersion = BuildVersionInfo.Current.DatabaseSchemaVersion,
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
        bool? refresh,
        int? w,
        IImageCache imageCache,
        IOptions<ImageCacheOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        if (!options.Value.Enabled)
        {
            return ImageSourceUrlPolicy.TryCreateAllowedUri(url, options.Value.AllowedHosts, out var sourceUri)
                ? Results.Redirect(sourceUri.AbsoluteUri)
                : Results.BadRequest();
        }

        try
        {
            var format = w is > 0
                && httpContext.Request.GetTypedHeaders().Accept?.Any(mediaType =>
                    string.Equals(mediaType.MediaType.Value, "image/webp", StringComparison.OrdinalIgnoreCase)) == true
                    ? ImageCacheFormat.Webp
                    : ImageCacheFormat.Original;
            var image = await imageCache.GetOrAddAsync(url, refresh == true, cancellationToken, w, format);
            var maxAgeSeconds = Math.Max(60, Convert.ToInt32(image.BrowserMaxAge.TotalSeconds));
            var entityTag = $"\"{image.CacheKey}\"";

            httpContext.Response.Headers.CacheControl = $"public, max-age={maxAgeSeconds}";
            httpContext.Response.Headers.Vary = "Accept";
            httpContext.Response.Headers.ETag = entityTag;
            httpContext.Response.Headers.LastModified = image.LastModifiedUtc.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

            if (RequestHasMatchingEntityTag(httpContext, entityTag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(
                File.OpenRead(image.FilePath),
                image.ContentType,
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
    });

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
