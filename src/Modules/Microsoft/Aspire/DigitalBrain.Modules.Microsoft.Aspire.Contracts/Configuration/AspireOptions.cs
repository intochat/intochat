using DigitalBrain.Contracts;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    public const string ConfigurationRoot = "DigitalBrain:Microsoft:Aspire";

    public string ApplicationName { get; set; } = "DigitalBrain";

    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ApplicationName);
}
