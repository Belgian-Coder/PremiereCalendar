namespace PremiereCalendar.Options;

public sealed class ApplicationUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string RepositoryPath { get; set; } = "";

    public string Remote { get; set; } = "origin";

    public string Branch { get; set; } = "feature/view-sync";

    public string InstallScriptPath { get; set; } = "Install-PremiereCalendar.ps1";

    public string UpdateScriptPath { get; set; } = "deploy/Update-And-Install-PremiereCalendar.ps1";

    public string LogDirectory { get; set; } = "App_Data/logs/application-updates";

    public string PowerShellPath { get; set; } = "powershell.exe";
}
