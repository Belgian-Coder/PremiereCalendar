namespace PremiereCalendar.IntegrationTests;

/// <summary>
/// Tests that decode images share ImageMagick's process-wide native resource limits (see ImageMagickRuntime), so they
/// must not run at the same time as each other. Tests of one collection run sequentially; other tests still run in parallel.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ImageMagickCollection
{
    public const string Name = "ImageMagick (process-wide native resource limits)";
}
