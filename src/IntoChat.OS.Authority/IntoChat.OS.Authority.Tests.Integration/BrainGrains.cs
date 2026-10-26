using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Authority.Tests.Integration;

// Platform grains are not neurons, so a fact reaches them through the Authority's typed resolver.
// Grains reaches the test-only probe grains a fact defines for itself (allow-listed still-grains, 06-09).
internal static class BrainGrains
{
    extension(ITestBrain brain)
    {
        public AuthorityNeurons Platform => brain.Services.GetRequiredService<AuthorityNeurons>();
        public IntoChat.OS.Runtime.IStillGrains Grains => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IStillGrains>();
        public IntoChat.OS.Runtime.IPlainNeurons Plain => brain.Services.GetRequiredService<IntoChat.OS.Runtime.IPlainNeurons>();
    }
}
