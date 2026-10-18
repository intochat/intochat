namespace IntoChat.OS.Authority.Identity.Configuration;

public sealed class KernelCorsOptions
{
    public const string SectionName = "IntoChatOS:Cors";
    public string? AllowedOrigin { get; set; }
}
