using System.Reflection;
using DigitalBrain.Modules.Options;
using IntoChat.OS.Framework;
using Orleans;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class StorageRingFacts
{
    private static readonly Assembly Apps = Assembly.Load("IntoChat.OS.Modules.Catalog");

    // Brain state and platform authority remain separate storage boundaries in one runtime.
    [Fact]
    public void NeuronsPersistInClusterStorageAndPlatformGrainsInPlatformStorage()
    {
        var platform = typeof(IntoChat.OS.Authority.PlatformHosting).Assembly;
        var sites = new[] { typeof(DigitalBrain.Modules.Options.ModuleId).Assembly, platform, Apps }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => (type, storage: parameter.GetCustomAttribute<PersistentStateAttribute>()?.StorageName)))
            .Where(site => site.storage is not null)
            .ToArray();
        var offenders = sites
            .Where(site => site.type.Assembly != platform
                ? site.storage != IntoChatOSNames.ClusterGrainStorage
                : site.storage != IntoChatOSNames.PlatformGrainStorage)
            .Select(site => $"{site.type.FullName} -> {site.storage}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(Environment.NewLine, offenders));
        Assert.Contains(sites, site => site.type.Assembly == platform);
    }
}
