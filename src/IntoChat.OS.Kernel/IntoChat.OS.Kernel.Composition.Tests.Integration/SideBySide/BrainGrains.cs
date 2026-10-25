using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;

internal static class BrainGrains
{
    public static IGrainFactory Grains(this ITestBrain brain) => brain.Services.GetRequiredService<IGrainFactory>();
}
