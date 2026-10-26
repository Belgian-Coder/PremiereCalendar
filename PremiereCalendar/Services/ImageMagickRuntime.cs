using ImageMagick;
using ImageMagick.Configuration;

namespace PremiereCalendar.Services;

internal static class ImageMagickRuntime
{
    private const ulong MemoryLimitBytes = 512UL * 1024 * 1024;
    private static readonly object InitializationLock = new();
    private static bool _initialized;

    internal const int MaxFrameCount = 1000;
    internal static string? ConfigurationPath { get; private set; }

    public static void EnsureInitialized(string contentRootPath)
    {
        lock (InitializationLock)
        {
            if (_initialized)
            {
                return;
            }

            var policyPath = Path.Combine(AppContext.BaseDirectory, "ImageMagick", "policy.xml");
            var policy = File.ReadAllText(policyPath);
            var configPath = Path.Combine(contentRootPath, "App_Data", "cache", "ImageMagick");
            Directory.CreateDirectory(configPath);

            var publishedPolicyPath = Path.Combine(configPath, "policy.xml");
            var temporaryPolicyPath = $"{publishedPolicyPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporaryPolicyPath, policy);
                File.Move(temporaryPolicyPath, publishedPolicyPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPolicyPath))
                {
                    File.Delete(temporaryPolicyPath);
                }
            }

            var configFiles = ConfigurationFiles.Default;
            configFiles.Policy.Data = policy;
            MagickNET.Initialize(configFiles, configPath);

            // Keep native pixel caches bounded without spilling image data to
            // /tmp, which is deliberately small in the container deployment.
            ResourceLimits.Memory = MemoryLimitBytes;
            ResourceLimits.Disk = 0;
            ResourceLimits.Thread = 1;
            ResourceLimits.ListLength = MaxFrameCount;
            ConfigurationPath = configPath;
            _initialized = true;
        }
    }
}
