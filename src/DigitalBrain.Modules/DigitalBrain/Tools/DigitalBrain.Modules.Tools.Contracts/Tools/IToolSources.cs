using Orleans.Metadata;

namespace DigitalBrain.Tools;

// One per brain, keyed {brainId}/tools: the tool sources this brain can search and call. Only a source
// keyed inside the brain registers here; Read returns their last reported tool lists.
[Alias("tools.sources"), DefaultGrainType("tools.sources")]
public interface IToolSources : INeuron
{
    Task Register(ToolSourceEntry entry);
    Task Unregister(string key);
    [Orleans.Concurrency.ReadOnly, Orleans.Concurrency.AlwaysInterleave] Task<ToolSourceEntry[]> Read();
}
