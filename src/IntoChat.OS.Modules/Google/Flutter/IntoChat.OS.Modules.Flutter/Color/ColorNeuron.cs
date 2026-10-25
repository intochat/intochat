using System.Text.RegularExpressions;
using IntoChat.OS;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Color.Signals;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace IntoChat.OS.Modules.Flutter.Color;

[GrainType(UIVocabulary.ColorType)]
internal sealed class ColorNeuron([PersistentState("state", IntoChatOSNames.ClusterGrainStorage)] IPersistentState<ColorState> store)
    : Neuron<ColorState>(store), IColor
{
    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task Set(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        if (!Hex.IsMatch(hex)) { throw new ArgumentException("Color must be #RRGGBB.", nameof(hex)); }
        var next = Snapshot;
        next.Name = this.GetPrimaryKeyString();
        next.Version++;
        next.Hex = hex.ToUpperInvariant();
        return Save(next, new ColorChanged(this.GetPrimaryKeyString(), next.Hex));
    }

    [ReadOnly] public Task<ColorState> Read() { Snapshot.Name = this.GetPrimaryKeyString(); return Task.FromResult(Snapshot); }
}
