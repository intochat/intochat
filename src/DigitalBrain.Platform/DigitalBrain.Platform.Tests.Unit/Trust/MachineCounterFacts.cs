using DigitalBrain.Platform.Trust;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R65: the signed-call counter lives in a file beside the identity key.
public sealed class MachineCounterFacts
{
    [Fact]
    public void ACounterSurvivesARestartAndNeverGoesBackwards()
    {
        var path = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}", "counter");
        var first = new MachineCounter(path);
        var (one, two) = (first.Next(), first.Next());

        var restarted = new MachineCounter(path);
        var three = restarted.Next();

        Assert.True(one < two && two < three, $"{one} < {two} < {three}");
        Assert.Equal(two + 1, three);
    }
}
