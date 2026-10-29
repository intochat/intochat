namespace IntoChat.Cloud.Mail;

public enum MailProvider
{
    Acs,
    Log,
}

public sealed class MailModuleOptions
{
    public MailProvider Provider { get; set; } = MailProvider.Acs;

    public string SenderAddress { get; set; } = "";

    public string SenderName { get; set; } = "";

    public string PublicBaseUrl { get; set; } = "https://app.intochat.io";

    public string DefaultLanguage { get; set; } = "en";
}
