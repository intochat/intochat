using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Microsoft.CSharp;

public sealed class CSharpAuthoringModuleHosting : ModuleHosting<DigitalBrain.Microsoft.CSharp.CSharpAuthoringModule>
{
    public override void Configure(DigitalBrainBuilder brain) => ArgumentNullException.ThrowIfNull(brain);
}
