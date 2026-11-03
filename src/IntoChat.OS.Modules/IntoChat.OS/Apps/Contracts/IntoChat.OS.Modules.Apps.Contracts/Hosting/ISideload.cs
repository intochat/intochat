namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Contracts.NeuronAlias("apps.sideload"), DigitalBrain.Contracts.DefaultNeuronType("apps.sideload")]
public interface ISideload
{
    [DigitalBrain.ReadOnlyCall]
    Task<SideloadRow[]> List();
    Task<SideloadResult> Install(string? name, string? consent = null);
    Task<SideloadResult> AddGit(string? url);
}

[GenerateSerializer, Alias("apps.sideload-row")]
public sealed record SideloadRow(
    [property: Id(0)] string Name,
    [property: Id(1)] string Title,
    [property: Id(2)] bool Installed);

[GenerateSerializer, Alias("apps.sideload-result")]
public sealed record SideloadResult(
    [property: Id(0)] bool Ok,
    [property: Id(1)] string Message)
{
    public static SideloadResult Done(string message) => new(true, message);
    public static SideloadResult Fail(string message) => new(false, message);
}
