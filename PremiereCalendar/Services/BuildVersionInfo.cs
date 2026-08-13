using System.Reflection;

namespace PremiereCalendar.Services;

public sealed record BuildVersionInfo(
    string Version,
    string InformationalVersion,
    string? SourceRevision,
    string? BuildId,
    DateTimeOffset? BuildTimeUtc,
    int DatabaseSchemaVersion)
{
    public static BuildVersionInfo Current { get; } = Create(typeof(BuildVersionInfo).Assembly);

    internal static BuildVersionInfo Create(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0-local";
        var normalized = informational.Split('+', 2)[0];
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(attribute => attribute.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);
        var buildTime = metadata.TryGetValue("BuildTimeUtc", out var buildTimeValue)
            && DateTimeOffset.TryParse(buildTimeValue, out var parsedBuildTime)
                ? (DateTimeOffset?)parsedBuildTime
                : null;

        return new BuildVersionInfo(
            normalized,
            informational,
            ValueOrNull(metadata, "SourceRevisionId"),
            ValueOrNull(metadata, "BuildId"),
            buildTime,
            DatabaseSchema.CurrentVersion);
    }

    private static string? ValueOrNull(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
