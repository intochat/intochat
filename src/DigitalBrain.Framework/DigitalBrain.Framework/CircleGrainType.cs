namespace DigitalBrain.Framework;

// The circle neuron's grain type. The Circles module declares it and the kernel's compute-share guard
// compares the calling grain's type to it.
public static class CircleGrainType
{
    public const string Name = "circles.circle";
}
