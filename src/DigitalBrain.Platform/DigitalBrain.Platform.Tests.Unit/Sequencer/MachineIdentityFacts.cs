using DigitalBrain.Platform.Sequencer;

namespace DigitalBrain.Platform.Tests.Unit.Sequencer;

public sealed class MachineIdentityFacts
{
    [Fact]
    public void AnIdentityIsStableAcrossLoadsAndItsSignaturesVerify()
    {
        var path = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}.key");
        try
        {
            using var first = MachineIdentity.LoadOrCreate(path);
            using var again = MachineIdentity.LoadOrCreate(path);
            Assert.Equal(first.MachineId, again.MachineId);
            var signature = first.Sign([1, 2, 3]);
            Assert.True(MachineIdentity.Verify(again.PublicKey, [1, 2, 3], signature));
            Assert.False(MachineIdentity.Verify(again.PublicKey, [1, 2, 4], signature));
        }
        finally { File.Delete(path); }
    }
}
