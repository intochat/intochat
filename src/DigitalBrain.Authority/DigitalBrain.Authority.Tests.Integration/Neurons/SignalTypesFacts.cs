using DigitalBrain.Authority.Neurons;
using DigitalBrain.Testing.Neurons;

namespace DigitalBrain.Authority.Tests.Integration.Neurons;

public sealed class SignalTypesFacts
{
    private static TypeEntry Entry(Type signal) => new(signal.FullName!, signal.FullName!, TypeKind.Signal, "test", "", [], []);

    [Fact]
    public void ASignalIsResolvedOnlyAmongTheBrainsOwnTypesAndAMissIsNotRemembered()
    {
        Assert.Throws<ArgumentException>(() => SignalTypes.Find(nameof(Pulsed), []));
        Assert.Equal(typeof(Pulsed), SignalTypes.Find(nameof(Pulsed), [Entry(typeof(Pulsed))]));
        Assert.Equal(typeof(Pulsed), SignalTypes.Find(typeof(Pulsed).FullName!, [Entry(typeof(Pulsed))]));
        Assert.Throws<ArgumentException>(() => SignalTypes.Find("Nope", [Entry(typeof(Pulsed))]));
    }
}
