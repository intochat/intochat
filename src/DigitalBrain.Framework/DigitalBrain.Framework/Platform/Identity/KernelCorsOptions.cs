namespace DigitalBrain.Authority.Identity.Configuration;

public sealed class KernelCorsOptions
{
    public const string SectionName = "DigitalBrain:Cors";
    public string? AllowedOrigin { get; set; }
}
