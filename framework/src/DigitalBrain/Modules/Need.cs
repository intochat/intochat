namespace DigitalBrain;

/// <summary>The resource kinds a module can need from its host.</summary>
public enum NeedKind
{
    /// <summary>A named state store.</summary>
    State,

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

    /// <summary>A named, metered outbound HTTP client.</summary>
    HttpClient,

    /// <summary>An inbound webhook route forwarded to a target neuron type.</summary>
    Webhook,

    /// <summary>A container image needed by the module.</summary>
    Container,
}

/// <summary>A resource a module needs from its host.</summary>
public sealed record Need
{
    private Need(NeedKind kind, string? name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>The resource kind.</summary>
    public NeedKind Kind { get; }

    /// <summary>The resource name or route path, when applicable.</summary>
    public string? Name { get; }

    /// <summary>Optional setting section or key holding the base address for <see cref="NeedKind.HttpClient"/>.</summary>
    public string? BaseAddressSetting { get; init; }

    /// <summary>Optional rate limit in requests per minute for <see cref="NeedKind.HttpClient"/>.</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>The target neuron type alias for <see cref="NeedKind.Webhook"/>.</summary>
    public string? TargetNeuronType { get; init; }

    /// <summary>The container image for <see cref="NeedKind.Container"/>.</summary>
    public string? Image { get; init; }

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

    /// <summary>A named state store.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> does not match <c>^[a-z][a-z0-9-]{0,62}$</c>.</exception>
    public static Need State(string name) => new(NeedKind.State, Names.Require(name, nameof(name), "state store"));

    /// <summary>A named, metered outbound HTTP client.</summary>
    public static Need HttpClient(string name, string? baseAddressSetting = null, int requestsPerMinute = 120)
        => new(NeedKind.HttpClient, Names.Require(name, nameof(name), "http client"))
        {
            BaseAddressSetting = baseAddressSetting,
            RequestsPerMinute = requestsPerMinute,
        };

    /// <summary>An inbound webhook route forwarded to <paramref name="targetNeuronType"/>.</summary>
    public static Need Webhook(string path, string targetNeuronType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new(NeedKind.Webhook, path)
        {
            TargetNeuronType = Names.Require(targetNeuronType, nameof(targetNeuronType), "neuron type"),
        };
    }

    /// <summary>A container image needed by the module.</summary>
    public static Need Container(string name, string image)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        return new(NeedKind.Container, Names.Require(name, nameof(name), "container"))
        {
            Image = image,
        };
    }
}
