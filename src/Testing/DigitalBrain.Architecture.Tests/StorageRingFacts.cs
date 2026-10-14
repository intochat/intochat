using System.Reflection;
using DigitalBrain.Framework;
using Orleans;

namespace DigitalBrain.Architecture.Tests;

public sealed class StorageRingFacts
{
    private static readonly Assembly Apps = Assembly.Load("DigitalBrain.Modules.Apps");

    // Brain state and platform authority remain separate storage boundaries in one runtime.
    [Fact]
    public void NeuronsPersistInClusterStorageAndPlatformGrainsInPlatformStorage()
    {
        var platform = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        var sites = new[] { typeof(Neuron).Assembly, platform, Apps }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => (type, storage: parameter.GetCustomAttribute<PersistentStateAttribute>()?.StorageName)))
            .Where(site => site.storage is not null)
            .ToArray();
        var offenders = sites
            .Where(site => typeof(Neuron).IsAssignableFrom(site.type) || site.type.Assembly != platform
                ? site.storage != DigitalBrainNames.ClusterGrainStorage
                : site.storage != DigitalBrainNames.PlatformGrainStorage)
            .Select(site => $"{site.type.FullName} -> {site.storage}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
        Assert.Contains(sites, site => site.type.Assembly == platform);
    }
}
