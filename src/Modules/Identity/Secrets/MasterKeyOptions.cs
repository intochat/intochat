namespace IntoChat.OS.Modules.Identity.Secrets;

public sealed class MasterKeyOptions
{
    public const string SectionName = "IntoChatOS";
    public string MasterKey { get; set; } = "";
}
