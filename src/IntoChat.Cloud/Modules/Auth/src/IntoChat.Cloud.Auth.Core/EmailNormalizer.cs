namespace IntoChat.Cloud.Auth.Core;

public static class EmailNormalizer
{
    public const int MaxLength = 255;

    /// <summary>Trim and lowercase; null when empty, over 255 chars or without an @ between non-empty parts.</summary>
    public static string? Normalize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length > MaxLength)
        {
            return null;
        }

        var at = normalized.IndexOf('@');
        return at <= 0 || at == normalized.Length - 1 ? null : normalized;
    }
}