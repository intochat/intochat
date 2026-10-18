using DigitalBrain.Framework;
using DigitalBrain.Authority.Storage;
using Microsoft.Extensions.Options;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Authority.Hosted;

// Platform account, principal and integration keys are not brain keys; one hosted allocation owns this journal.
internal sealed class HostedPlatformGrainStorage : IGrainStorage
{
    private readonly BrainGrainStorage inner;
    internal string Scope { get; }

    public HostedPlatformGrainStorage(BrainLogs logs, Serializer serializer, IOptions<HostedRuntimeOptions> options)
    {
        var hosted = options.Value;
        if (!hosted.IsConfigured) { throw new InvalidOperationException("Durable hosted platform storage requires an allocation scope."); }
        Scope = BrainScope.Create(hosted.AccountId, hosted.BrainId).Id;
        inner = new BrainGrainStorage(logs, serializer, Scope);
    }

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => inner.ReadStateAsync(stateName, grainId, grainState);
    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => inner.WriteStateAsync(stateName, grainId, grainState);
    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState) => inner.ClearStateAsync(stateName, grainId, grainState);
}
