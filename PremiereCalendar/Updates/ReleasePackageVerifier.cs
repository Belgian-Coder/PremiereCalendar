using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace PremiereCalendar.Updates;

public static class ReleasePackageVerifier
{
    public static ReleaseManifest ReadAndVerifyManifest(string manifestPath, string packagePath, X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(certificate);
        if (new FileInfo(manifestPath).Length > 1024 * 1024)
            throw new InvalidDataException("Release manifest is too large.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Release manifest is empty.");
        if (manifest.SchemaVersion != 1 || !Version.TryParse(manifest.Version, out _) ||
            !string.Equals(manifest.Channel, "stable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release manifest identity is invalid.");
        if (Path.GetFileName(manifest.PackageFileName) != manifest.PackageFileName ||
            !manifest.PackageFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release package filename is unsafe.");
        using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        var hash = Convert.ToHexString(SHA256.HashData(packageStream));
        if (!hash.Equals(manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release package hash does not match the manifest.");
        using var rsa = certificate.GetRSAPublicKey() ?? throw new InvalidDataException("Pinned certificate has no RSA public key.");
        var payload = string.Join("\n", manifest.SchemaVersion, manifest.Version, manifest.Channel,
            manifest.PackageFileName, manifest.PackageSha256.ToUpperInvariant(), manifest.MinimumDatabaseSchemaVersion,
            manifest.MaximumDatabaseSchemaVersion, (manifest.ReleaseNotes ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n'));
        if (!rsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(manifest.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("Release manifest signature is invalid.");
        return manifest;
    }

    public static void ExtractSafely(string archivePath, string destination, long maxExpandedBytes = 2L * 1024 * 1024 * 1024)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10_000) throw new InvalidDataException("Release archive contains too many entries.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains(':') || segments.Any(s => s is "." or "..") || !seen.Add(name))
                throw new InvalidDataException("Release archive contains an unsafe path.");
            expanded = checked(expanded + entry.Length);
            if (expanded > maxExpandedBytes) throw new InvalidDataException("Release archive expands beyond the configured limit.");
            var target = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release archive escaped destination.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: false);
        }
    }
}
