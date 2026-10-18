using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration;

// Platform grains are not neurons, so a fact reaches them through the Authority's typed resolver.
// Grains stays for the test-only probe grains a fact defines for itself.
internal static class BrainGrains
{
    extension(ITestBrain brain)
    {
        public AuthorityNeurons Platform => brain.Services.GetRequiredService<AuthorityNeurons>();
        public IGrainFactory Grains => brain.Services.GetRequiredService<IGrainFactory>();
    }
}
