namespace PremiereCalendar.Services;

/// <summary>
/// The view-sync URL this browser tab last published or applied for a route, with the group and revision it carried.
/// A record without a matching group id counts as unseen.
/// </summary>
public sealed record ViewSyncLastSeen(string Url, long Revision, string? GroupId = null);
