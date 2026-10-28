using DigitalBrain;
using IntoChat.OS.Authority.Neurons;
using IntoChat.OS.Testing.Neurons;

namespace IntoChat.OS.Authority.Tests.Integration.Neurons;

public sealed class SignalTypesFacts
{
    private static BrainType Entry(Type signal) => new(signal.FullName!, signal.FullName!, DigitalBrain.TypeKind.Signal, "test", "", [], []);

    [Fact]
    public void ASignalIsResolvedOnlyAmongTheBrainsOwnTypesAndAMissIsNotRemembered()
    {
        Assert.Throws<ArgumentException>(() => SignalTypes.Find(nameof(Pulsed), []));
        Assert.Equal(typeof(Pulsed), SignalTypes.Find(nameof(Pulsed), [Entry(typeof(Pulsed))]));
        Assert.Equal(typeof(Pulsed), SignalTypes.Find(typeof(Pulsed).FullName!, [Entry(typeof(Pulsed))]));
        Assert.Throws<ArgumentException>(() => SignalTypes.Find("Nope", [Entry(typeof(Pulsed))]));
    }
}
