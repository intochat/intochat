using DigitalBrain.Modules.Options;

namespace IntoChat.OS.Modules.Template;

public sealed class TemplateOptions : IModuleOptions
{
    [Setting]
    public string DefaultPrefix { get; set; } = "Echo";

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DefaultPrefix);
    }
}
