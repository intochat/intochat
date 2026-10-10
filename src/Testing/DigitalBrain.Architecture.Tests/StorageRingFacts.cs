using System.Reflection;
using DigitalBrain.Framework;
using Orleans;

namespace DigitalBrain.Architecture.Tests;

public sealed class StorageRingFacts
{
    private static readonly Assembly Apps = Assembly.Load("DigitalBrain.Modules.Apps");

    // Neurons keyed outside every brain that hold no brain's data: trust-cluster services (ruling R1), which a
    // machine cannot host because it holds only brain parts (Q27).
    [Fact]
    public void NeuronsPersistInClusterStorageAndPlatformGrainsInPlatformStorage()
    {
        var platform = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        var trust = typeof(DigitalBrain.Platform.TrustHosting).Assembly;
        var sites = new[] { typeof(Neuron).Assembly, platform, trust, Apps }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => (type, storage: parameter.GetCustomAttribute<PersistentStateAttribute>()?.StorageName)))
            .Where(site => site.storage is not null)
            .ToArray();
        var offenders = sites
            .Where(site => typeof(Neuron).IsAssignableFrom(site.type) || (site.type.Assembly != platform && site.type.Assembly != trust)
                ? site.storage != DigitalBrainNames.ClusterGrainStorage
                : site.storage != DigitalBrainNames.PlatformGrainStorage)
            .Select(site => $"{site.type.FullName} -> {site.storage}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
        Assert.Contains(sites, site => site.type.Assembly == trust);
    }
}
