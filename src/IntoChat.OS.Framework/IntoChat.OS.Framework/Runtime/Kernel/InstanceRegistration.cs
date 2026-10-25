using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;
using IntoChat.OS.Contracts.Configuration;

namespace IntoChat.OS.Framework;

// Wraps the cluster grain storage: a neuron's first persisted write registers it in its brain's
// instance directory, clearing its state retires it. There is no other registration path.
internal sealed class InstanceRegistration(IGrainStorage inner, IGrainFactory grains, IGrainContextAccessor context) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    internal IGrainStorage Inner => inner;

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => inner.ReadStateAsync(stateName, grainId, grainState);

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        // Registered before the first write lands: once the record exists nothing would retry a lost
        // registration, while a registration whose write fails is retired again. Register is
        // idempotent and the directory is another grain that never calls back into this one.
        var directory = grainState.RecordExists ? null : Directory(grainId);
        if (directory is null) { await inner.WriteStateAsync(stateName, grainId, grainState); return; }
        var key = grainId.Key.ToString()!;
        await directory.Register(key, CallerContextStamper.TryGet(out var caller) ? caller.PrincipalId : null);
        try { await inner.WriteStateAsync(stateName, grainId, grainState); }
        catch
        {
            await directory.Retire(key);
            throw;
        }
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await inner.ClearStateAsync(stateName, grainId, grainState);
        if (Directory(grainId) is { } directory) { await directory.Retire(grainId.Key.ToString()!); }
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        if (inner is ILifecycleParticipant<ISiloLifecycle> participant) { participant.Participate(lifecycle); }
    }

    // Only neurons inside a brain are instances: "{brain}/..." where the first segment is a brain id.
    // Kernel grains, workers and other grains never register, nor do keys outside every brain: a
    // bare key (the brain itself, account-keyed neurons) or a "@platform/..." key (the marketplace, meters).
    private IInstanceDirectory? Directory(GrainId grainId)
    {
        if (context.GrainContext?.GrainInstance is not Neuron) { return null; }
        if (BrainKey.ScopeOf(grainId.Key.ToString()!) is not { } brain) { return null; }
        return grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(brain, grainId.Type.ToString()!));
    }

    // Replaces the keyed cluster storage registration with this decorator around it. False when no
    // cluster storage is registered yet: MS DI cannot decorate a registration that comes later. A
    // registration that is already the decorator stays as it is, so decorating twice wraps once.
    public static bool TryDecorate(IServiceCollection services)
    {
        var original = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IGrainStorage)
            && Equals(descriptor.ServiceKey, IntoChatOSNames.ClusterGrainStorage));
        if (original is null) { return false; }
        if (original.KeyedImplementationFactory?.Target is Decorated) { return true; }
        services.Remove(original);
        services.AddKeyedSingleton<IGrainStorage>(IntoChatOSNames.ClusterGrainStorage, new Decorated(original).Create);
        return true;
    }

    // Puts back the descriptor TryDecorate wrapped, for a host that recomposes the cluster storage afterwards.
    public static bool TryUndecorate(IServiceCollection services)
    {
        var wrapped = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IGrainStorage)
            && Equals(descriptor.ServiceKey, IntoChatOSNames.ClusterGrainStorage));
        if (wrapped?.KeyedImplementationFactory?.Target is not Decorated decorated) { return false; }
        services[services.IndexOf(wrapped)] = decorated.Original;
        return true;
    }

    private sealed class Decorated(ServiceDescriptor original)
    {
        public ServiceDescriptor Original => original;

        public IGrainStorage Create(IServiceProvider provider, object? key) =>
            new InstanceRegistration(InstanceRegistration.Create(original, provider, key), provider.GetRequiredService<IGrainFactory>(), provider.GetRequiredService<IGrainContextAccessor>());
    }

    private static IGrainStorage Create(ServiceDescriptor original, IServiceProvider provider, object? key) =>
        (IGrainStorage)(original.KeyedImplementationInstance
            ?? original.KeyedImplementationFactory?.Invoke(provider, key)
            ?? ActivatorUtilities.CreateInstance(provider, original.KeyedImplementationType!));
}
