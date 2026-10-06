namespace DigitalBrain;

/// <summary>
/// A signal type's name: its <see cref="AliasAttribute"/> value, such as <c>time.tick</c>.
/// </summary>
/// <remarks>
/// Exists because connections are stored in grain state, and stored state cannot hold a
/// <see cref="Type"/>: the storage serializer refuses it, and a class rename would orphan every
/// stored connection. The alias is the rename-safe name Orleans itself uses for the type.
/// </remarks>
[GenerateSerializer, Alias("signal.id"), Immutable]
public readonly record struct SignalId
{
    /// <summary>The signal type's alias.</summary>
    [Id(0)] public string Alias { get; }

    /// <exception cref="ArgumentException"><paramref name="alias"/> is empty.</exception>
    public SignalId(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        Alias = alias;
    }

    public override string ToString() => Alias;
}
