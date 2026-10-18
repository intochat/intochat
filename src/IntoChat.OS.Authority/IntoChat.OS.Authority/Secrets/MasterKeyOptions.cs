namespace IntoChat.OS.Authority.Secrets;

public sealed class MasterKeyOptions
{
    public const string SectionName = "IntoChatOS";
    public string MasterKey { get; set; } = "";
}
