using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using Orleans.Metadata;

namespace DigitalBrain.Microsoft.Aspire;

// Only the key-authenticated AppHost bridge endpoint reports; neurons and scripts see IAspire.
// Not a neuron: enforcement would otherwise judge the report against a brain scope the
// deployment-global AppHost does not have, and refuse it.
[Alias("microsoft.aspire.reporter"), DefaultGrainType("microsoft.aspire")]
internal interface IAspireResourceReporter : IGrainWithStringKey
{
    Task Report(AspireResource resource);
    Task ReportMany(AspireResource[] resources);
}
