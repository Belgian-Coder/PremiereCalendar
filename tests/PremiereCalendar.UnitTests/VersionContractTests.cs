using System.Reflection;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class VersionContractTests
{
    [Fact]
    public void DevelopmentBuildUsesNeutralVersionAndAuthoritativeSchema()
    {
        Assert.StartsWith("0.0.0-local", BuildVersionInfo.Current.InformationalVersion, StringComparison.Ordinal);
        Assert.Equal("0.0.0-local", BuildVersionInfo.Current.Version);
        Assert.Equal(DatabaseSchema.CurrentVersion, BuildVersionInfo.Current.DatabaseSchemaVersion);
        Assert.Equal(DatabaseSchema.Migrations.Max(migration => migration.Version), DatabaseSchema.CurrentVersion);
    }

    [Fact]
    public void DatabaseSchemaAssemblyMetadataMatchesRegisteredMigrations()
    {
        var metadata = typeof(DatabaseSchema).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "DatabaseSchemaVersion").Value;
        Assert.Equal(DatabaseSchema.Migrations.Max(migration => migration.Version).ToString(), metadata);
    }
}
