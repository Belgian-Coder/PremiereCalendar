using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

internal static class TestSqliteDatabase
{
    public static void Initialize(string root, string path)
    {
        new SqliteDatabaseInitializer(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = path, MigrationBackupDirectory = Path.Combine(root, "backups") }),
            new TestEnvironment(root),
            NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync().GetAwaiter().GetResult();
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
