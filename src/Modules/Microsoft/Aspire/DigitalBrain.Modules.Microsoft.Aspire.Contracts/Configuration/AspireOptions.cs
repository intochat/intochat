using DigitalBrain.Contracts;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    public static readonly string ConfigurationRoot = DigitalBrainConfiguration.ModuleSection(new ModuleId("aspire"));

    public string ApplicationName { get; set; } = "DigitalBrain";

    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ApplicationName);
}
