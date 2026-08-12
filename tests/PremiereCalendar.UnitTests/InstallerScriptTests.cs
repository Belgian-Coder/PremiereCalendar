using System.Runtime.CompilerServices;

namespace PremiereCalendar.UnitTests;

public sealed class InstallerScriptTests
{
    [Theory]
    [InlineData("deploy/Install-PremiereCalendarService.ps1")]
    [InlineData("deploy/release/Install-PremiereCalendar.ps1")]
    [InlineData("deploy/release/Uninstall-PremiereCalendar.ps1")]
    public void FirewallScriptsRemoveStaleRulesForTheConfiguredDisplayName(string relativePath)
    {
        var script = ReadRepoFile(relativePath);

        Assert.Contains("function Remove-PremiereCalendarFirewallRules", script, StringComparison.Ordinal);
        Assert.Contains("[regex]::Escape($DisplayName)", script, StringComparison.Ordinal);
        Assert.Contains("Remove-PremiereCalendarFirewallRules -DisplayName $DisplayName", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceInstallerFirewallRuleNameUsesConfiguredDisplayNameAndPort()
    {
        var script = ReadRepoFile("deploy/Install-PremiereCalendarService.ps1");

        Assert.DoesNotContain(
            "$firewallRuleName = 'Premiere Calendar 5298'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$firewallRuleName = \"$DisplayName $Port\"",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseInstallerRemovesLegacyServiceCredentialEnvironmentKeys()
    {
        var script = ReadRepoFile("deploy/release/Install-PremiereCalendar.ps1");

        Assert.Contains("$legacyCredentialEnvironmentKeys = @(", script, StringComparison.Ordinal);
        Assert.Contains("'Tmdb__BearerToken'", script, StringComparison.Ordinal);
        Assert.Contains("'Omdb__ApiKey'", script, StringComparison.Ordinal);
        Assert.Contains("'Fanart__ApiKey'", script, StringComparison.Ordinal);
        Assert.Contains("'TheTvdb__ApiKey'", script, StringComparison.Ordinal);
        Assert.Contains("'Watchmode__ApiKey'", script, StringComparison.Ordinal);
        Assert.Contains("'Trakt__ClientSecret'", script, StringComparison.Ordinal);
        Assert.Contains("'Simkl__ClientSecret'", script, StringComparison.Ordinal);
        Assert.Contains("'Simkl__AccessToken'", script, StringComparison.Ordinal);
        Assert.Contains("$environment.Remove($key)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildReleasePackageClearsSettingsOnlyCredentialKeys()
    {
        var script = ReadRepoFile("deploy/Build-ReleasePackage.ps1");

        Assert.Contains("Get-ChildItem -LiteralPath $publishDirectory -Filter 'appsettings*.json'", script, StringComparison.Ordinal);
        Assert.Contains("Clear-ReleaseSecrets $_.FullName", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Tmdb', 'BearerToken') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Omdb', 'ApiKey') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Fanart', 'ApiKey') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Trakt', 'ClientId') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Trakt', 'ClientSecret') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('TheTvdb', 'ApiKey') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Watchmode', 'ApiKey') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Simkl', 'ClientId') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Simkl', 'ClientSecret') ''", script, StringComparison.Ordinal);
        Assert.Contains("Set-HashtableValue $config @('Simkl', 'AccessToken') ''", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceInstallScriptsAreWindowsPowerShellFriendlyAndHonorCustomUrls()
    {
        var rootInstaller = ReadRepoFile("Install-PremiereCalendar.ps1");
        var serviceInstaller = ReadRepoFile("deploy/Install-PremiereCalendarService.ps1");
        var publishScript = ReadRepoFile("deploy/Publish-PremiereCalendar.ps1");

        Assert.DoesNotContain("??", rootInstaller, StringComparison.Ordinal);
        Assert.Contains("$environment.Add(\"Urls=http://0.0.0.0:$Port\")", serviceInstaller, StringComparison.Ordinal);
        Assert.Contains("$env:Urls = \"http://0.0.0.0:$Port\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $startedProcess.Id -Force", publishScript, StringComparison.Ordinal);
        Assert.Contains("robocopy $resolvedPublishOutput $resolvedTargetDirectory /MIR /XD App_Data", publishScript, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceUpdateScriptOnlyFastForwardsCleanRepositoriesBeforeInstalling()
    {
        var script = ReadRepoFile("deploy/Update-And-Install-PremiereCalendar.ps1");

        Assert.Contains("[string]$Branch = 'main'", script, StringComparison.Ordinal);
        Assert.Contains("safe.directory=$resolvedRepositoryPath", script, StringComparison.Ordinal);
        Assert.Contains("status --porcelain=v1", script, StringComparison.Ordinal);
        Assert.Contains("Repository has local changes", script, StringComparison.Ordinal);
        Assert.Contains("fetch $Remote $Branch", script, StringComparison.Ordinal);
        Assert.Contains("merge-base --is-ancestor HEAD $remoteRef", script, StringComparison.Ordinal);
        Assert.Contains("pull --ff-only $Remote $Branch", script, StringComparison.Ordinal);
        Assert.Contains("Install-PremiereCalendar.ps1", script, StringComparison.Ordinal);
        Assert.Contains("Backup snapshot:", script, StringComparison.Ordinal);
        Assert.Contains("Test-ApplicationHealth -Uri $HealthUrl", script, StringComparison.Ordinal);
        Assert.Contains("reset --hard $previousHead", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Rollback completed to $previousHead", script, StringComparison.Ordinal);
        Assert.DoesNotContain("checkout --", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildReleasePackagePublishesRequestedVersionMetadata()
    {
        var script = ReadRepoFile("deploy/Build-ReleasePackage.ps1");

        Assert.Contains("/p:Version=$Version", script, StringComparison.Ordinal);
        Assert.Contains("/p:InformationalVersion=$Version", script, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubReleasePublisherRequiresReviewedSourceAndSignedAssets()
    {
        var script = ReadRepoFile("eng/Publish-GitHubRelease.ps1");

        Assert.Contains("branch --show-current", script, StringComparison.Ordinal);
        Assert.Contains("status --porcelain=v1", script, StringComparison.Ordinal);
        Assert.Contains("main must exactly match origin/main", script, StringComparison.Ordinal);
        Assert.Contains("Version $Version must be newer", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained", script, StringComparison.Ordinal);
        Assert.Contains("restore (Join-Path $projectRoot 'PremiereCalendar\\PremiereCalendar.csproj') -r win-x64", script, StringComparison.Ordinal);
        Assert.Contains("stable.manifest.json", script, StringComparison.Ordinal);
        Assert.Contains("RSASignaturePadding", script, StringComparison.Ordinal);
        Assert.Contains("RSACertificateExtensions]::GetRSAPrivateKey", script, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt", script, StringComparison.Ordinal);
        Assert.Contains("tag -a $tag", script, StringComparison.Ordinal);
        Assert.Contains("push origin $tag", script, StringComparison.Ordinal);
        Assert.Contains("--verify-tag", script, StringComparison.Ordinal);
        Assert.Contains("--draft", script, StringComparison.Ordinal);
        Assert.Contains("--draft=false", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseUpdaterPinsTrustAndRollsBackVersionedActivation()
    {
        var script = ReadRepoFile("deploy/Updates/update-helper.ps1");

        Assert.Contains("administrator-pinned certificate", script, StringComparison.Ordinal);
        Assert.Contains("Release manifest signature is invalid", script, StringComparison.Ordinal);
        Assert.Contains("RSACertificateExtensions]::GetRSAPublicKey", script, StringComparison.Ordinal);
        Assert.Contains("Release archive contains an unsafe path", script, StringComparison.Ordinal);
        Assert.Contains("New-Item -ItemType Junction", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Service", script, StringComparison.Ordinal);
        Assert.Contains("Wait-ForHealthyVersion", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $previous -Destination $current", script, StringComparison.Ordinal);
        Assert.Contains("$previousServicePath", script, StringComparison.Ordinal);
        Assert.Contains("sc.exe config $ServiceName binPath= $previousServicePath", script, StringComparison.Ordinal);
        Assert.Contains("$serviceWasCreated", script, StringComparison.Ordinal);
        Assert.Contains("Urls=http://0.0.0.0:$Port", script, StringComparison.Ordinal);
        Assert.Contains("PremiereCalendarData", script, StringComparison.Ordinal);
        Assert.Contains("$databaseStateCaptured", script, StringComparison.Ordinal);
        Assert.Contains("pre-$version-", script, StringComparison.Ordinal);
        Assert.Contains("must not be filesystem roots", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Stop-Service -Name $ServiceName", StringComparison.Ordinal)
            < script.IndexOf("Copy-Item -Path (Join-Path $legacyData '*')", StringComparison.Ordinal));
    }

    [Fact]
    public void GitHubUpdaterDownloadsManifestBeforeExactDeclaredPackage()
    {
        var script = ReadRepoFile("deploy/Updates/install-github-release.ps1");

        var manifestDownload = script.IndexOf("stable.manifest.json", StringComparison.Ordinal);
        var packageDeclaration = script.IndexOf("manifest.packageFileName", StringComparison.Ordinal);
        Assert.True(manifestDownload >= 0 && packageDeclaration > manifestDownload);
        Assert.Contains("No administrator-pinned release certificate exists", script, StringComparison.Ordinal);
        Assert.Contains("browser_download_url", script, StringComparison.Ordinal);
        Assert.Contains("size -gt 1GB", script, StringComparison.Ordinal);
        Assert.Contains("Add-Type -AssemblyName System.Net.Http", script, StringComparison.Ordinal);
        Assert.Contains("System.Net.Http.HttpCompletionOption]::ResponseHeadersRead", script, StringComparison.Ordinal);
        Assert.Contains("Release asset exceeds its size limit", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-WebRequest -Uri $assets[$packageName]", script, StringComparison.Ordinal);
        Assert.Contains("is already the latest stable release", script, StringComparison.Ordinal);
        Assert.Contains("Refusing to downgrade", script, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, relativePath));
    }
}
