using DigitalBrain.Contracts;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Runtime;

/// <summary>Resolves an os full key (<c>{brain}/{key}</c>) through <see cref="INeuronProxies"/>.</summary>
public static class NeuronProxies
{
    /// <summary>The neuron <paramref name="osFullKey"/> names for <typeparamref name="T"/>.</summary>
    public static T Get<T>(this INeuronProxies proxies, string osFullKey) where T : class
    {
        ArgumentNullException.ThrowIfNull(proxies);
        return proxies.Get<T>(PlainNeuronContracts.IdOf(typeof(T), osFullKey));
    }
}
