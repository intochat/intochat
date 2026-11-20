namespace IntoChat.OS.Host.Http;

public sealed class KernelCorsOptions
{
    public const string SectionName = "IntoChatOS:Cors";
    public string? AllowedOrigin { get; set; }
}
