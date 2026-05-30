namespace PremiereCalendar.Models;

public sealed record RottenTomatoesScores(
    int? CriticScore,
    int? AudienceScore)
{
    public static RottenTomatoesScores Empty { get; } = new(null, null);

    public bool HasAnyScore => CriticScore is not null || AudienceScore is not null;
}
