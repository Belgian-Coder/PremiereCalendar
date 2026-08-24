namespace PremiereCalendar.Options;

public sealed class AppDatabaseOptions
{
    public string Provider { get; set; } = "Sqlite";
    public string Path { get; set; } = "App_Data/data/premiere-calendar.db";
    public string ConnectionString { get; set; } = "";
    public string PasswordFile { get; set; } = "";
    public string MigrationBackupDirectory { get; set; } = "App_Data/backups/database-migrations";
    public int MigrationBackupRetentionCount { get; set; } = 10;
}
