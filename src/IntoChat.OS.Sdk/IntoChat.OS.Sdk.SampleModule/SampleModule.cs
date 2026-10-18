using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Sdk.SampleModule;

// The reference module: attributes declare the shape, the generator writes the rest, OnConfigure is the only code.
// References only the Sdk; the same source is a generator golden (SampleModule.g.cs).
[Module("sample")]
[Integration("sample", "Sample")]
[Secret("ApiKey")]
public sealed partial class SampleModule : Module<SampleOptions>
{
    partial void OnConfigure(IModuleServices services) => services.Services.AddSingleton(services.Options<SampleOptions>());
}

public sealed class SampleOptions : IModuleOptions
{
    [Setting] public string Region { get; init; } = "eu";

    public void Validate() { }
}
