using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Runtime;
using BrainId = DigitalBrain.BrainId;
using DigitalBrain;

namespace IntoChat.OS.Kernel.Composition;

// INeuronResolver over the grain factory while neurons are grains. Keys pass through verbatim: Resolve composes
// "{brain}/{relative}" exactly as NeuronId.Within does, and ResolveKey uses the key as given.
internal sealed class GrainNeuronResolver(IGrainFactory grains) : INeuronResolver
{
    public T Resolve<T>(BrainId brain, string relative) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        return ResolveKey<T>($"{brain.Value}/{relative}");
    }

    public T ResolveKey<T>(string fullKey) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(fullKey);
        return (T)(object)grains.GetGrain(typeof(T), fullKey);
    }
}
