using System.Globalization;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var ids = new[] { "Europe/Brussels", "US/Eastern", "America/New_York" };
var zones = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
foreach (var id in ids)
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById(id);
    zones[id] = zone;
    Console.WriteLine($"Resolved {id}: id={zone.Id}, baseUtcOffset={zone.BaseUtcOffset}, supportsDst={zone.SupportsDaylightSavingTime}");
}
Require(File.Exists("/usr/share/zoneinfo/Europe/Brussels"), "Missing runtime zoneinfo file /usr/share/zoneinfo/Europe/Brussels");
static DateTimeOffset Utc(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
static string Convert(TimeZoneInfo zone, string value) => TimeZoneInfo.ConvertTime(Utc(value), zone).ToString("yyyy-MM-dd'T'HH:mm:ss zzz", CultureInfo.InvariantCulture);
var spring = Convert(zones["Europe/Brussels"], "2026-03-29T01:00:00Z");
var fall = Convert(zones["Europe/Brussels"], "2026-10-25T01:00:00Z");
Require(spring == "2026-03-29T03:00:00 +02:00", $"Unexpected Brussels spring conversion: {spring}");
Require(fall == "2026-10-25T02:00:00 +01:00", $"Unexpected Brussels fall conversion: {fall}");
Console.WriteLine($"Brussels spring: 2026-03-29T01:00:00Z => {spring}");
Console.WriteLine($"Brussels fall: 2026-10-25T01:00:00Z => {fall}");
Console.WriteLine("RESULT=PASS");
