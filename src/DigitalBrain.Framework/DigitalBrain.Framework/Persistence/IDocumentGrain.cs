using Orleans;

namespace DigitalBrain.Framework;

public interface IDocumentGrain : IGrainWithStringKey
{
    Task<(long Version, string? Payload)> ReadAsync();
    Task<bool> TryWriteAsync(long expectedVersion, string payload);
}
