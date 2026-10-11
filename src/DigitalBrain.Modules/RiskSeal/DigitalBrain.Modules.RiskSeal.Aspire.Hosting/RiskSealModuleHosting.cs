using DigitalBrain.Aspire.Hosting;

namespace DigitalBrain.RiskSeal.Aspire.Hosting;

public sealed class RiskSealModuleHosting : ModuleHosting<DigitalBrain.RiskSeal.RiskSealModule>
{
    public override void Configure(DigitalBrainBuilder brain)
        => new DigitalBrainModuleBuilder<RiskSealModuleHosting>(brain).WithRiskSeal();
}
