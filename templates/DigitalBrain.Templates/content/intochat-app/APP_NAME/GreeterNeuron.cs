using DigitalBrain;

namespace APP_NAMESPACE;

/// <summary>Published when the greeter greets someone.</summary>
[Signal("APP_ID.greeted")]
public sealed record Greeted(string Name) : Signal;

/// <summary>Greets by publishing <see cref="Greeted"/>.</summary>
[Neuron("greeter")]
public sealed class GreeterNeuron : Neuron
{
    /// <summary>Greets <paramref name="name"/>.</summary>
    public Task Greet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Publish(new Greeted(name));
    }
}
