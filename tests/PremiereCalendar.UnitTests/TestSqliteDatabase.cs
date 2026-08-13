using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

internal static class TestSqliteDatabase
{
    public static void Initialize(string root, string relativePath)
    {
        Directory.CreateDirectory(root);
        var initializer = new SqliteDatabaseInitializer(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions
            {
                Path = relativePath,
                MigrationBackupDirectory = Path.Combine(root, "backups")
            }),
            new TestEnvironment(root),
            new DatabaseRecoveryState(),
            TimeProvider.System,
            NullLogger<SqliteDatabaseInitializer>.Instance);
        initializer.InitializeAsync().GetAwaiter().GetResult();
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
