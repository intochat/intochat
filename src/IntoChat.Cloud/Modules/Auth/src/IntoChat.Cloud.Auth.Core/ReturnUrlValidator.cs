namespace IntoChat.Cloud.Auth.Core;

public sealed class ReturnUrlValidator(IEnumerable<string> allowedOrigins)
{
    private readonly HashSet<string> _origins = allowedOrigins
        .Select(origin => origin.TrimEnd('/'))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl.Any(char.IsControl))
        {
            return false;
        }

        if (returnUrl[0] == '/')
        {
            return returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\');
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return _origins.Contains(uri.GetLeftPart(UriPartial.Authority));
    }
}