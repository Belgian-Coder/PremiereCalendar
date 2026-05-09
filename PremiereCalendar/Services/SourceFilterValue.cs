using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public static class SourceFilterValue
{
    private const string ProviderPrefix = "provider:";
    private const string NetworkPrefix = "network:";

    public static string Provider(int id, string name)
    {
        return EncodedValue(ProviderPrefix, id, name);
    }

    public static string Providers(IEnumerable<int> ids, string name)
    {
        var normalizedIds = ids
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();

        return normalizedIds.Length == 1
            ? Provider(normalizedIds[0], name)
            : EncodedValue(ProviderPrefix, string.Join(',', normalizedIds), name);
    }

    public static string Network(int id, string name)
    {
        return EncodedValue(NetworkPrefix, id, name);
    }

    public static bool TryGetProviderId(string value, out int providerId)
    {
        if (TryGetProviderIds(value, out var providerIds) && providerIds.Length > 0)
        {
            providerId = providerIds[0];
            return true;
        }

        providerId = 0;
        return false;
    }

    public static bool TryGetProviderIds(string value, out int[] providerIds)
    {
        return TryParseIds(value, ProviderPrefix, out providerIds, out _);
    }

    public static bool TryGetNetworkId(string value, out int networkId)
    {
        return TryParse(value, NetworkPrefix, out networkId, out _);
    }

    public static string Label(string value)
    {
        if (TryParseIds(value, ProviderPrefix, out _, out var providerName)
            || TryParse(value, NetworkPrefix, out _, out providerName))
        {
            return string.IsNullOrWhiteSpace(providerName) ? value : providerName;
        }

        return value;
    }

    public static bool Matches(PremiereItem item, string selected)
    {
        if (TryGetProviderIds(selected, out var providerIds))
        {
            return item.Sources.Any(source => source.Id is { } sourceId
                    && providerIds.Contains(sourceId)
                    && IsWatchProviderKind(source.Kind))
                || SourceNames(item).Any(source => string.Equals(source, Label(selected), StringComparison.OrdinalIgnoreCase));
        }

        if (TryGetNetworkId(selected, out var networkId))
        {
            return item.Sources.Any(source => source.Id == networkId && string.Equals(source.Kind, "network", StringComparison.OrdinalIgnoreCase))
                || SourceNames(item).Any(source => string.Equals(source, Label(selected), StringComparison.OrdinalIgnoreCase));
        }

        return SourceNames(item).Any(source => string.Equals(source, selected, StringComparison.OrdinalIgnoreCase));
    }

    public static string? OptionValue(PremiereSource source)
    {
        if (source.Id is > 0 && IsWatchProviderKind(source.Kind))
        {
            return Provider(source.Id.Value, source.Name);
        }

        if (source.Id is > 0 && string.Equals(source.Kind, "network", StringComparison.OrdinalIgnoreCase))
        {
            return Network(source.Id.Value, source.Name);
        }

        return string.IsNullOrWhiteSpace(source.Name) ? null : source.Name;
    }

    private static string EncodedValue(string prefix, int id, string name)
    {
        return $"{prefix}{id}:{name.Trim()}";
    }

    private static string EncodedValue(string prefix, string ids, string name)
    {
        return $"{prefix}{ids}:{name.Trim()}";
    }

    private static bool TryParse(string value, string prefix, out int id, out string name)
    {
        id = 0;
        name = "";

        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = value[prefix.Length..];
        var separator = remainder.IndexOf(':', StringComparison.Ordinal);
        var idPart = separator < 0 ? remainder : remainder[..separator];
        if (string.IsNullOrWhiteSpace(idPart)
            || !int.TryParse(idPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id)
            || id <= 0)
        {
            id = 0;
            return false;
        }

        name = separator < 0 ? "" : remainder[(separator + 1)..].Trim();
        return true;
    }

    private static bool TryParseIds(string value, string prefix, out int[] ids, out string name)
    {
        ids = [];
        name = "";

        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = value[prefix.Length..];
        var separator = remainder.IndexOf(':', StringComparison.Ordinal);
        var idsPart = separator < 0 ? remainder : remainder[..separator];
        if (string.IsNullOrWhiteSpace(idsPart))
        {
            return false;
        }

        ids = idsPart
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(
                part,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id)
                ? id
                : 0)
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToArray();

        name = separator < 0 ? "" : remainder[(separator + 1)..].Trim();
        return ids.Length > 0;
    }

    private static bool IsWatchProviderKind(string kind)
    {
        return kind.Equals("flatrate", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("free", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("ads", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("rent", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("buy", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("provider", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SourceNames(PremiereItem item)
    {
        return item.SourceNames
            .Concat([item.NetworkName, item.WebChannelName])
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source!);
    }
}
