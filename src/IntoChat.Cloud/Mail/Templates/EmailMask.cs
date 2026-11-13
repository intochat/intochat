namespace IntoChat.Cloud.Mail.Templates;

public static class EmailMask
{
    public static string Mask(string email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return "***";
        }

        var at = email.IndexOf('@');
        return at < 1 ? "***" : $"{email[0]}***@{email[(at + 1)..]}";
    }
}
