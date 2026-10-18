using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Person.Signals;
using DigitalBrain.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace DigitalBrain.Flutter.Person;

[GrainType(UIVocabulary.PersonType)]
internal sealed class PersonNeuron([PersistentState("state", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PersonState> store)
    : Neuron<PersonState>(store), IPerson, ISharedPersonReader
{
    public Task Set(string displayName, string avatarUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(avatarUrl);
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.DisplayName = displayName.Trim();
        next.AvatarUrl = avatarUrl.Trim();
        return Save(next, new PersonChanged(this.GetPrimaryKeyString(), next.Version));
    }

    [ReadOnly] public Task<PersonState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}

// Module-only leaf access after the viewer Space neuron authorizes the exact reference.
[Orleans.Metadata.DefaultGrainType(UIVocabulary.PersonType)]
[Alias("ui.shared-person-reader")]
internal interface ISharedPersonReader : IGrainWithStringKey
{
    Task<PersonState> Read();
}
