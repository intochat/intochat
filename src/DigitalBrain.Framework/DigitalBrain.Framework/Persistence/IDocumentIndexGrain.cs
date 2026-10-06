using Orleans;

namespace DigitalBrain.Framework;

public interface IDocumentIndexGrain : IGrainWithStringKey
{
    Task<string[]> ListAsync();
    Task AddAsync(string id);
}
