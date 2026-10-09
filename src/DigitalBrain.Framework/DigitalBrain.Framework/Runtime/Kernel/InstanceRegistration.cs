using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace DigitalBrain.Framework;

// Wraps the Default grain storage: a neuron's first persisted write registers it in its brain's
// instance directory, clearing its state retires it. There is no other registration path.
internal sealed class InstanceRegistration(IGrainStorage inner, IGrainFactory grains, IGrainContextAccessor context) : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
{
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => inner.ReadStateAsync(stateName, grainId, grainState);

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var first = !grainState.RecordExists;
        await inner.WriteStateAsync(stateName, grainId, grainState);
        // Awaited, not fire-and-forget: the directory is another grain that never calls back into
        // this one synchronously, and a lost registration could not be retried once the record exists.
        if (first && Directory(grainId) is { } directory)
        {
            await directory.Register(grainId.Key.ToString()!, CallerContextStamper.TryGet(out var caller) ? caller.PrincipalId : null);
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

    // Only neurons are instances: kernel grains, workers and other grains never register.
    private IInstanceDirectory? Directory(GrainId grainId)
    {
        if (context.GrainContext?.GrainInstance is not Neuron) { return null; }
        var key = grainId.Key.ToString();
        if (string.IsNullOrEmpty(key)) { return null; }
        return grains.GetGrain<IInstanceDirectory>(InstanceDirectoryKey.Of(BrainKey.BrainOf(key), grainId.Type.ToString()!));
    }

    // Replaces the keyed Default storage registration with this decorator around it. False when no
    // Default storage is registered yet: MS DI cannot decorate a registration that comes later.
    public static bool TryDecorate(IServiceCollection services)
    {
        var original = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IGrainStorage)
            && Equals(descriptor.ServiceKey, DigitalBrainNames.DefaultGrainStorage));
        if (original is null) { return false; }
        services.Remove(original);
        services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.DefaultGrainStorage, (provider, key) =>
            new InstanceRegistration(Create(original, provider, key), provider.GetRequiredService<IGrainFactory>(), provider.GetRequiredService<IGrainContextAccessor>()));
        return true;
    }

    private static IGrainStorage Create(ServiceDescriptor original, IServiceProvider provider, object? key) =>
        (IGrainStorage)(original.KeyedImplementationInstance
            ?? original.KeyedImplementationFactory?.Invoke(provider, key)
            ?? ActivatorUtilities.CreateInstance(provider, original.KeyedImplementationType!));
}
