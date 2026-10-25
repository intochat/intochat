using IntoChat.OS.Runtime;
using IntoChat.OS.Sdk;
using BrainId = DigitalBrain.BrainId;

namespace IntoChat.OS.Framework;

// INeuronResolver over the grain factory while neurons are grains. Keys pass through verbatim: Resolve composes
// "{brain}/{relative}" exactly as NeuronId.Within does, and ResolveKey uses the key as given.
internal sealed class GrainNeuronResolver(IGrainFactory grains) : INeuronResolver
{
    public T Resolve<T>(BrainId brain, NeuronId relative) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brain.Value);
        return ResolveKey<T>(relative.Within(brain.Value).Name);
    }

    public T ResolveKey<T>(string fullKey) where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(fullKey);
        return (T)(object)grains.GetGrain(typeof(T), fullKey);
    }
}
