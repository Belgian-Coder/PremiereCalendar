using System.Text.Json.Serialization;

namespace PremiereCalendar.Updates;

public sealed record ReleaseManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("packageFileName")] string PackageFileName,
    [property: JsonPropertyName("packageSha256")] string PackageSha256,
    [property: JsonPropertyName("releaseNotes")] string ReleaseNotes,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("minimumDatabaseSchemaVersion")] int MinimumDatabaseSchemaVersion = 0,
    [property: JsonPropertyName("maximumDatabaseSchemaVersion")] int MaximumDatabaseSchemaVersion = int.MaxValue);

public sealed record BuildMetadata(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sourceRevision")] string SourceRevision,
    [property: JsonPropertyName("buildId")] string BuildId,
    [property: JsonPropertyName("builtUtc")] DateTimeOffset BuiltUtc);
