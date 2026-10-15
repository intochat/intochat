using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Salesforce.Aspire.Hosting;

public sealed class SalesforceModuleHosting : ModuleHosting<DigitalBrain.Salesforce.SalesforceModule>
{
    public override void Configure(DigitalBrainBuilder brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
    }
}
