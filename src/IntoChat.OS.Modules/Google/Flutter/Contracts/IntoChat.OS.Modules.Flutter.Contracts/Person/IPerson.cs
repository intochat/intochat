using DigitalBrain.Contracts;
using DigitalBrain;
using IntoChat.OS;
using DigitalBrain.State;

namespace IntoChat.OS.Modules.Flutter.Person;

[NeuronAlias("person"), DefaultNeuronType(UIVocabulary.PersonType)]
public interface IPerson
{
    Task Set(string displayName, string avatarUrl);
    [DigitalBrain.ReadOnlyCall] Task<PersonState> Read();
}

[GenerateSerializer, Alias("ui.person-state")]
[ExportableState]
public sealed class PersonState
{
    [Id(0)] public string Name { get; set; } = "";
    [Id(1)] public int Version { get; set; }
    [Id(2)] public string DisplayName { get; set; } = "";
    [Id(3)] public string AvatarUrl { get; set; } = "";
}
