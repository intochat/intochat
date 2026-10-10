using System.Reflection;
using DigitalBrain.Framework;
using Orleans;

namespace DigitalBrain.Architecture.Tests;

public sealed class StorageRingFacts
{
    [Fact]
    public void NeuronsPersistInClusterStorageAndPlatformGrainsInPlatformStorage()
    {
        var platform = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        var offenders = new[] { typeof(Neuron).Assembly, platform }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => (type, storage: parameter.GetCustomAttribute<PersistentStateAttribute>()?.StorageName)))
            .Where(site => site.storage is not null)
            .Where(site => typeof(Neuron).IsAssignableFrom(site.type) || site.type.Assembly != platform
                ? site.storage != DigitalBrainNames.ClusterGrainStorage
                : site.storage != DigitalBrainNames.PlatformGrainStorage)
            .Select(site => $"{site.type.FullName} -> {site.storage}")
            .ToArray();
        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
    }
}
