using DigitalBrain;
using IntoChat.OS;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoToolsFacts
{
    [Fact]
    public void DisabledModulesDoNotPreventAnAnswerOrExpandToolAuthority()
    {
        BrainType[] available =
        [
            new("text", "text", DigitalBrain.TypeKind.Neuron, "ui", "", [], []),
            new("other", "other", DigitalBrain.TypeKind.Neuron, "private", "", [], []),
            new("signal", "signal", DigitalBrain.TypeKind.Signal, "ui", "", [], []),
        ];
        Assert.Equal(["text"], InoTools.Select(["postgres", "text", "signal"], available));
        Assert.Empty(InoTools.Select(["postgres"], available));
    }
}
