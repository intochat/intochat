using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

public sealed class SeenVersionsFacts
{
    [Fact]
    public void AnOlderHeadThanOnceSeenIsARollbackEvenAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seen-{Guid.NewGuid():N}.json");
        try
        {
            new SeenVersions(path).Saw("p", 7);
            var restarted = new SeenVersions(path);

            restarted.Check("p", 7);
            restarted.Check("p", 9);
            Assert.Throws<RollbackDetectedException>(() => restarted.Check("p", 6));
        }
        finally { File.Delete(path); }
    }
}
