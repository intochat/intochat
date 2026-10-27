namespace IntoChat.OS.Connections.Identity.Configuration;

public sealed class KernelCorsOptions
{
    public const string SectionName = "IntoChatOS:Cors";
    public string? AllowedOrigin { get; set; }
}
