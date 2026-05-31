namespace PremiereCalendar.Options;

public sealed class ApplicationUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string RepositoryPath { get; set; } = "";

    public string Remote { get; set; } = "origin";

    public string Branch { get; set; } = "main";

    public string InstallScriptPath { get; set; } = "Install-PremiereCalendar.ps1";

    public string UpdateScriptPath { get; set; } = "deploy/Update-And-Install-PremiereCalendar.ps1";

    public string LogDirectory { get; set; } = "App_Data/logs/application-updates";

    public string PowerShellPath { get; set; } = "powershell.exe";

    public string TargetDirectory { get; set; } = "D:\\Apps\\PremiereCalendar";

    public string BackupDirectory { get; set; } = "D:\\Apps\\PremiereCalendar\\App_Data\\backups\\application-updates";

    public string HealthUrl { get; set; } = "http://localhost:5298/health";

    public bool RollbackOnFailure { get; set; } = true;
}
