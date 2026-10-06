using DigitalBrain;
using DigitalBrain.Framework;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireOptions : IModuleOptions
{
    public static readonly string ConfigurationRoot = DigitalBrainConfiguration.ModuleSection(new ModuleId("aspire"));

    public string ApplicationName { get; set; } = AppDomain.CurrentDomain.FriendlyName;

    public void Validate() => ArgumentException.ThrowIfNullOrWhiteSpace(ApplicationName);
}
