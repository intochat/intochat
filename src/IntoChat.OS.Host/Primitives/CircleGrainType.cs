
namespace IntoChat.OS.Host.Primitives;

// The legacy circle neuron's type. The Circles module declares it for read-only legacy circle brains and
// the kernel's compute-share guard compares the calling neuron's type to it.
public static class CircleGrainType
{
    public const string Name = "circles.circle";
}
