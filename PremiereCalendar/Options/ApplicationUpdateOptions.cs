namespace PremiereCalendar.Options;

public sealed class ApplicationUpdateOptions
{
    public bool Enabled { get; set; } = true;

    public string UpdaterScriptPath { get; set; } = "D:\\Apps\\PremiereCalendar\\updater\\install-github-release.ps1";

    public string LogDirectory { get; set; } = "D:\\Apps\\PremiereCalendarData\\logs\\application-updates";

    public string PowerShellPath { get; set; } = "powershell.exe";

    public string InstallRoot { get; set; } = "D:\\Apps\\PremiereCalendar";

    public string DataRoot { get; set; } = "D:\\Apps\\PremiereCalendarData";

    public string Repository { get; set; } = "Belgian-Coder/PremiereCalendar";
}
