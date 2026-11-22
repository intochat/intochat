namespace DigitalBrain.Testing;

/// <summary>
/// A named set of edge fake registrations applied when starting a <see cref="JourneyOS"/>.
/// </summary>
public sealed class JourneyEnvironment
{
    private readonly Action<FakeSet> _configure;

    private JourneyEnvironment(string name, Action<FakeSet> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        Name = name;
        _configure = configure;
    }

    /// <summary>The name identifying this environment.</summary>
    public string Name { get; }

    /// <summary>Returns the default local environment with no edge overrides.</summary>
    public static JourneyEnvironment Local() => new("local", static _ => { });

    /// <summary>Returns a named test environment configured by <paramref name="configure"/>.</summary>
    public static JourneyEnvironment Test(string name, Action<FakeSet> configure)
        => new(name, configure);

    internal void Configure(FakeSet fakes)
    {
        ArgumentNullException.ThrowIfNull(fakes);
        _configure(fakes);
    }
}
