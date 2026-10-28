using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

// A feature loaded into a brain: an app's program, from the features folder, the Marketplace or the
// Creator. Owner is the publisher it came from, or the brain itself for one authored in place.
[GenerateSerializer, Alias("kernel.feature")]
public sealed record Feature([property: Id(0)] string Name, [property: Id(1)] string Version, [property: Id(2)] string Owner)
{
    // How the types a feature brings name it in BrainType.Provider.
    public string Provider => $"feature:{Owner}/{Name}";
}
