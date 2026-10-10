using System.Reflection;
using DigitalBrain.Framework;
using DigitalBrain.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Storage;

namespace DigitalBrain.Architecture.Tests;

// R33: the trust cluster composes no neuron. Every assembly the trust host loads, except the Framework that defines
// Neuron, holds none, and the trust registrations name no `cluster` storage for one to persist in.
public sealed class TrustCompositionFacts
{
    [Fact]
    public void TheTrustHostComposesNoNeuron()
    {
        var neurons = Closure(typeof(DigitalBrain.Trust.Program).Assembly)
            .Where(assembly => assembly != typeof(Neuron).Assembly)
            .SelectMany(Types)
            .Where(type => typeof(Neuron).IsAssignableFrom(type))
            .Select(type => type.FullName)
            .ToArray();
        Assert.Empty(neurons);

        var builder = Host.CreateApplicationBuilder();
        builder.UseOrleans(silo => silo.AddTrustCluster());
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IGrainStorage)
            && Equals(descriptor.ServiceKey, DigitalBrainNames.ClusterGrainStorage));
    }

    private static IEnumerable<Assembly> Closure(Assembly root)
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal) { [root.GetName().Name!] = root };
        var pending = new Stack<Assembly>([root]);
        while (pending.TryPop(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!(reference.Name ?? "").StartsWith("DigitalBrain", StringComparison.Ordinal) || seen.ContainsKey(reference.Name!)) { continue; }
                var loaded = Assembly.Load(reference);
                seen[reference.Name!] = loaded;
                pending.Push(loaded);
            }
        }
        return seen.Values;
    }

    private static Type[] Types(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException partial) { return [.. partial.Types.OfType<Type>()]; }
    }
}
