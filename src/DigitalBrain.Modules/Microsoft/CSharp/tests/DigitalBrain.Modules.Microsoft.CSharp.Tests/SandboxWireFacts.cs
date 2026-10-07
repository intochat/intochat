using DigitalBrain.Microsoft.CSharp.Sandbox;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

// The Sandbox image is built alone and cannot share a file with the module, so each side keeps its
// own copy of the HTTP shapes; this fact is what keeps the two honest.
public sealed class SandboxWireFacts
{
    private static string[] Shape(Type type)
        => [.. type.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => $"{property.Name}:{property.PropertyType}")];

    [Fact]
    public void TheBrainsCopyOfTheRunRequestMatchesTheImagesCopy()
        => Assert.Equal(Shape(typeof(RunRequest)), Shape(typeof(SandboxRunRequest)));

    [Fact]
    public void TheBrainsCopyOfTheRunStatusMatchesTheImagesCopy()
        => Assert.Equal(Shape(typeof(RunStatus)), Shape(typeof(SandboxRunStatus)));

    [Fact]
    public void TheRunStatesTheBrainComparesAreTheOnesTheImageSends()
        => Assert.Equal([RunStatuses.Running, RunStatuses.Exited], [SandboxRunStates.Running, SandboxRunStates.Exited]);
}
