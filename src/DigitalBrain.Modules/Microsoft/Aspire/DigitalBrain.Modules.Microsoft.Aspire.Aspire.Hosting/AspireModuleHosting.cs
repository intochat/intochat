using DigitalBrain;
using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Microsoft.Aspire;

public sealed class AspireModuleHosting : ModuleHosting<DigitalBrain.Microsoft.Aspire.AspireModule>
{
    public override void Configure(DigitalBrainBuilder brain) => ArgumentNullException.ThrowIfNull(brain);
}
