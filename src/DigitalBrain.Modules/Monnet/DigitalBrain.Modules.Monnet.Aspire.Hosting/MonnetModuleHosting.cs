using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Monnet.Aspire.Hosting;

public sealed class MonnetModuleHosting : ModuleHosting<MonnetModule>
{
    public override void Configure(DigitalBrainBuilder brain)
        => new DigitalBrainModuleBuilder<MonnetModuleHosting>(brain).WithMonnet();
}
