using System.Globalization;
using System.Text;
using System.Text.Json;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

/// <summary>Deterministic, side-effect-free exports for the currently visible calendar items.</summary>
public static class CalendarExportService
{
    public static string ToIcs(
        IEnumerable<PremiereItem> items,
        string calendarName = "Premiere Calendar",
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        var stamp = (generatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var sb = new StringBuilder("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\nPRODID:-//PremiereCalendar//Export//EN\r\n");
        sb.Append("X-WR-CALNAME:").Append(IcsEscape(calendarName)).Append("\r\n");
        foreach (var item in items)
        {
            var uid = string.IsNullOrWhiteSpace(item.CanonicalId) ? $"{item.MediaType}:{item.TmdbId}:{item.PremiereDate:yyyyMMdd}" : item.CanonicalId;
            sb.Append("BEGIN:VEVENT\r\nUID:").Append(IcsEscape($"{uid}@premiere-calendar")).Append("\r\nDTSTAMP:")
                .Append(stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)).Append("\r\nDTSTART;VALUE=DATE:")
                .Append(item.PremiereDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\nDTEND;VALUE=DATE:")
                .Append(item.PremiereDate.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append("\r\nSUMMARY:")
                .Append(IcsEscape(item.Title)).Append("\r\nDESCRIPTION:").Append(IcsEscape(Description(item))).Append("\r\nEND:VEVENT\r\n");
        }
        return sb.Append("END:VCALENDAR\r\n").ToString();
    }

    public static string ToCsv(IEnumerable<PremiereItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder("id,title,date,type,mediaType,tmdbId,url\r\n");
        foreach (var item in items)
            sb.AppendJoin(',', Csv(item.CanonicalId), Csv(item.Title), item.PremiereDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Csv(item.Type.ToString()), Csv(item.MediaType.ToString()), item.TmdbId.ToString(CultureInfo.InvariantCulture), Csv(item.TmdbUrl ?? item.ExternalUrl ?? "")).Append("\r\n");
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<PremiereItem> items, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        return JsonSerializer.Serialize(items, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string Description(PremiereItem item) => string.Join("\n", new[] { item.Overview, item.TmdbUrl ?? item.ExternalUrl }.Where(static x => !string.IsNullOrWhiteSpace(x)));
    private static string Csv(string value)
    {
        var safeValue = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? $"'{value}" : value;
        return safeValue.Contains(',', StringComparison.Ordinal)
            || safeValue.Contains('"')
            || safeValue.Contains('\r')
            || safeValue.Contains('\n')
                ? $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
                : safeValue;
    }
    private static string IcsEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal).Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);
}
