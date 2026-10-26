namespace DigitalBrain;

/// <summary>The resource kinds a module can need from its host.</summary>
public enum NeedKind
{
    /// <summary>A named grain store.</summary>
    GrainStore,

    /// <summary>Streams.</summary>
    Streams,

    /// <summary>Durable reminders.</summary>
    Reminders,

    /// <summary>Secret storage.</summary>
    Secrets,

    /// <summary>Outbound HTTP.</summary>
    Http,

    /// <summary>A code sandbox.</summary>
    Sandbox,

    /// <summary>A language model.</summary>
    Model,
}

/// <summary>A resource a module needs. Only <see cref="NeedKind.GrainStore"/> carries a name.</summary>
public sealed record Need
{
    private Need(NeedKind kind, string? name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>The resource kind.</summary>
    public NeedKind Kind { get; }

    /// <summary>The store name, for <see cref="NeedKind.GrainStore"/> only.</summary>
    public string? Name { get; }

    /// <summary>Streams.</summary>
    public static Need Streams { get; } = new(NeedKind.Streams, null);

    /// <summary>Durable reminders.</summary>
    public static Need Reminders { get; } = new(NeedKind.Reminders, null);

    /// <summary>Secret storage.</summary>
    public static Need Secrets { get; } = new(NeedKind.Secrets, null);

    /// <summary>Outbound HTTP.</summary>
    public static Need Http { get; } = new(NeedKind.Http, null);

    /// <summary>A code sandbox.</summary>
    public static Need Sandbox { get; } = new(NeedKind.Sandbox, null);

    /// <summary>A language model.</summary>
    public static Need Model { get; } = new(NeedKind.Model, null);

    /// <summary>A named grain store.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not match <c>^[a-z][a-z0-9-]{0,62}$</c>.</exception>
    public static Need GrainStore(string name) => new(NeedKind.GrainStore, Names.Require(name, nameof(name), "grain store"));
}
