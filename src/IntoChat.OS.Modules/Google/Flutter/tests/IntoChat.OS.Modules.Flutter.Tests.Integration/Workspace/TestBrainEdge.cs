using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Spi;
using IntoChat.OS.Modules.Flutter.Workspace;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Workspace;

internal static class TestBrainEdge
{
    public static BrainEdge Of(ITestBrain brain)
        => new(brain.Services.GetRequiredService<INeuronProxies>(), brain.Kernel, brain);
}
