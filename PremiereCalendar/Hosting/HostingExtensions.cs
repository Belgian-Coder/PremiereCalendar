using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Hosting;

public static class HostingExtensions
{
    public static IServiceCollection AddHostingHardening(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Only consume forwarded headers from explicitly configured proxies/networks.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Add(System.Net.IPAddress.Loopback);
            options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
            foreach (var proxy in configuration.GetSection("Hosting:ForwardedProxies").Get<string[]>() ?? [])
            {
                if (System.Net.IPAddress.TryParse(proxy, out var address)) options.KnownProxies.Add(address);
            }
        });
        services.AddOptions<AppDatabaseOptions>().Bind(configuration.GetSection("AppDatabase"))
            .Validate(o => string.Equals(o.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
                || string.Equals(o.Provider, "PostgreSql", StringComparison.OrdinalIgnoreCase),
                "AppDatabase:Provider must be Sqlite or PostgreSql")
            .Validate(o => !string.Equals(o.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(o.Path), "AppDatabase:Path is required for SQLite")
            .Validate(o => !string.Equals(o.Provider, "PostgreSql", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(o.ConnectionString), "AppDatabase:ConnectionString is required for PostgreSQL")
            .Validate(o => !string.IsNullOrWhiteSpace(o.MigrationBackupDirectory), "AppDatabase:MigrationBackupDirectory is required")
            .Validate(o => o.MigrationBackupRetentionCount is >= 1 and <= 100, "AppDatabase migration backup retention must be between 1 and 100")
            .ValidateOnStart();
        services.AddOptions<ImageCacheOptions>().Bind(configuration.GetSection("ImageCache"))
            .Validate(o => !o.Enabled || (o.MaxBytes > 0 && o.MaxConcurrentDownloads > 0 && o.MaxConcurrentDecodes > 0 && o.AllowedHosts.Length > 0 && o.AllowedHosts.All(host => !string.IsNullOrWhiteSpace(host))),
                "Enabled image cache requires positive limits and at least one allowed host")
            .ValidateOnStart();
        return services;
    }

    public static IApplicationBuilder UseHostingHardening(this IApplicationBuilder app)
    {
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; " +
                "script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: blob: https:; font-src 'self' data:; connect-src 'self' https: ws: wss:;";
            await next();
        });
        return app;
    }
}
