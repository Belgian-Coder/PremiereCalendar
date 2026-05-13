namespace PremiereCalendar.Services;

public static class LocalReturnUrl
{
    public static bool IsSafe(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl[0] != '/')
        {
            return false;
        }

        if (returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\'))
        {
            return false;
        }

        foreach (var character in returnUrl)
        {
            if (character == '\\' || char.IsControl(character))
            {
                return false;
            }
        }

        return !returnUrl.Contains("://", StringComparison.Ordinal);
    }
}
