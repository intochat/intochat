namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Contracts.NeuronAlias("apps.installed-modules"), DigitalBrain.Contracts.DefaultNeuronType("apps.installed-modules")]
public interface IInstalledModules
{
    [DigitalBrain.Contracts.ReadOnlyCall]
    Task<InstalledModule[]> List();
}

[GenerateSerializer, Alias("apps.installed-module")]
public sealed record InstalledModule(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Capabilities);
