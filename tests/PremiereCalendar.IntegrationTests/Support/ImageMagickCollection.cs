namespace PremiereCalendar.IntegrationTests;

/// <summary>
/// Tests that decode images share ImageMagick's process-wide native resource limits (see ImageMagickRuntime), so they
/// must not run at the same time as each other. Under ParallelMode.All a [Collection] alone does not serialize; DisableParallelization
/// moves the collection to the non-parallel phase, where its tests run one after the other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageMagickCollection
{
    public const string Name = "ImageMagick (process-wide native resource limits)";
}
