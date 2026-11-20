
namespace IntoChat.OS.Host.Primitives;

// The app neuron's grain type. Modules that accept calls only from an app's own neuron (behavior
// binding, app storage, grant migration) compare the calling grain's type to this.
public static class AppGrainType
{
    public const string Name = "apps.app2";
}
