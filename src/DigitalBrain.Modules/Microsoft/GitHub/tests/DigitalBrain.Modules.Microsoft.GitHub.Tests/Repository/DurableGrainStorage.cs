using System.Collections.Concurrent;
using DigitalBrain.Framework;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace DigitalBrain.Modules.Microsoft.GitHub.Tests.Repository;

// The harness's memory storage lives in grains of the one silo, so a silo restart loses it. A
// deployment's storage outlives the silo; this one holds serialized bytes outside the cluster, so
// only what a grain actually wrote comes back after a restart.
internal sealed class DurableGrainStorage
{
    private readonly ConcurrentDictionary<(string StateName, GrainId GrainId), byte[]> _records = new();

    public void Register(ISiloBuilder silo)
        => silo.Services.AddKeyedSingleton<IGrainStorage>(DigitalBrainNames.DefaultGrainStorage,
            (services, _) => new Silo(_records, services.GetRequiredService<Serializer>()));

    private sealed class Silo(ConcurrentDictionary<(string, GrainId), byte[]> records, Serializer serializer) : IGrainStorage
    {
        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            if (records.TryGetValue((stateName, grainId), out var bytes))
            {
                grainState.State = serializer.Deserialize<T>(bytes);
                grainState.RecordExists = true;
                grainState.ETag = "stored";
            }
            return Task.CompletedTask;
        }

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            records[(stateName, grainId)] = serializer.SerializeToArray(grainState.State);
            grainState.RecordExists = true;
            grainState.ETag = "stored";
            return Task.CompletedTask;
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            records.TryRemove((stateName, grainId), out _);
            grainState.RecordExists = false;
            grainState.ETag = null;
            return Task.CompletedTask;
        }
    }
}
