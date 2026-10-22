using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace MODULE_NAMESPACE;

// Attributes declare the shape, the SDK generator writes the rest, OnConfigure is the only code.
[Module("MODULE_ID")]
[Integration("MODULE_ID", "MODULE_CLASS")]
[Secret("MODULE_SECRET")]
public sealed partial class MODULE_CLASSModule : Module<MODULE_CLASSOptions>
{
    partial void OnConfigure(IModuleServices services) => services.Services.AddSingleton(services.Options<MODULE_CLASSOptions>());
}

public sealed class MODULE_CLASSOptions : IModuleOptions
{
    [Setting] public string MODULE_SETTING { get; init; } = "eu";

    public void Validate() { }
}
