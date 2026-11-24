using IntoChat.OS.Modules.AI.Models;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Pure-data agent declaration on an <see cref="AppManifest"/> (Rung 1 agent app).
/// </summary>
public sealed record AppAgent(
    string Instructions,
    ModelSelection? Model = null,
    string[]? Starters = null,
    string[]? Tools = null)
{
    public bool Equals(AppAgent? other) => other is not null
        && string.Equals(Instructions, other.Instructions, StringComparison.Ordinal)
        && EqualityComparer<ModelSelection?>.Default.Equals(Model, other.Model)
        && SequenceEqual(Starters, other.Starters)
        && SequenceEqual(Tools, other.Tools);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Instructions, StringComparer.Ordinal);
        hash.Add(Model);
        AddSequence(ref hash, Starters);
        AddSequence(ref hash, Tools);
        return hash.ToHashCode();
    }

    private static bool SequenceEqual(string[]? left, string[]? right)
        => (left ?? []).SequenceEqual(right ?? [], StringComparer.Ordinal);

    private static void AddSequence(ref HashCode hash, string[]? items)
    {
        foreach (var item in items ?? [])
        {
            hash.Add(item, StringComparer.Ordinal);
        }
    }
}
