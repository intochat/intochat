namespace DigitalBrain.OS.Testing;

// The Production reference brain with the AI module's content capture switched off.
public sealed class ProductionCaptureOffFixture : ProductionBrainFixture
{
    protected override bool SensitiveCapture => false;
}
