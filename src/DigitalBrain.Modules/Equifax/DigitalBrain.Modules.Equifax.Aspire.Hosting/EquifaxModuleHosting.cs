using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.Equifax.Aspire.Hosting;

public sealed class EquifaxModuleHosting : ModuleHosting<DigitalBrain.Equifax.EquifaxModule>
{
    public override void Configure(DigitalBrainBuilder brain)
        => new DigitalBrainModuleBuilder<EquifaxModuleHosting>(brain).WithEquifax();
}
