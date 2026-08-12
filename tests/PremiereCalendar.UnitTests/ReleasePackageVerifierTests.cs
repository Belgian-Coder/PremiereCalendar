using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PremiereCalendar.Updates;

namespace PremiereCalendar.UnitTests;

public sealed class ReleasePackageVerifierTests
{
    [Fact]
    public void ReadAndVerifyManifest_accepts_signed_package_and_rejects_tampering()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PremiereCalendar test signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var packagePath = Path.Combine(root.FullName, "premiere-calendar-1.1.0-win-x64.zip");
            File.WriteAllBytes(packagePath, [1, 2, 3, 4]);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
            var payload = string.Join("\n", "1", "1.1.0", "stable", Path.GetFileName(packagePath), hash, "0", int.MaxValue, "notes");
            var signature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            var manifest = new ReleaseManifest(1, "1.1.0", "stable", Path.GetFileName(packagePath), hash, "notes", signature);
            var manifestPath = Path.Combine(root.FullName, "stable.manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

            var verified = ReleasePackageVerifier.ReadAndVerifyManifest(manifestPath, packagePath, certificate);
            Assert.Equal("1.1.0", verified.Version);

            File.WriteAllBytes(packagePath, [9, 9, 9]);
            Assert.Throws<InvalidDataException>(() =>
                ReleasePackageVerifier.ReadAndVerifyManifest(manifestPath, packagePath, certificate));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void ExtractSafely_rejects_traversal()
    {
        var root = Directory.CreateTempSubdirectory();
        var zip = Path.Combine(root.FullName, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("bad");
        Assert.Throws<InvalidDataException>(() => ReleasePackageVerifier.ExtractSafely(zip, Path.Combine(root.FullName, "out")));
    }

    [Fact]
    public void ExtractSafely_extracts_normal_archive()
    {
        var root = Directory.CreateTempSubdirectory(); var zip = Path.Combine(root.FullName, "ok.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("app/version.txt").Open())) writer.Write("1.0.0");
        var output = Path.Combine(root.FullName, "out"); ReleasePackageVerifier.ExtractSafely(zip, output);
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(output, "app", "version.txt")));
    }
}
