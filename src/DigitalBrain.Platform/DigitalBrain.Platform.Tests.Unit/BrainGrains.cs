using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Platform.Tests.Unit;

// Platform grains are not neurons, so a fact reaches them through the host's grain factory.
internal static class BrainGrains
{
    extension(ITestBrain brain)
    {
        public IGrainFactory Grains => brain.Services.GetRequiredService<IGrainFactory>();
    }
}
