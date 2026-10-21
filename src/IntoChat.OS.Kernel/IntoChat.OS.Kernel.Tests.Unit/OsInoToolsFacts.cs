using IntoChat.OS;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoToolsFacts
{
    [Fact]
    public void DisabledModulesDoNotPreventAnAnswerOrExpandToolAuthority()
    {
        TypeEntry[] available =
        [
            new("text", "text", TypeKind.Neuron, "ui", "", [], []),
            new("other", "other", TypeKind.Neuron, "private", "", [], []),
            new("signal", "signal", TypeKind.Signal, "ui", "", [], []),
        ];
        Assert.Equal(["text"], InoTools.Select(["postgres", "text", "signal"], available));
        Assert.Empty(InoTools.Select(["postgres"], available));
    }
}
