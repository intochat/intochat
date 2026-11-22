using System.ComponentModel;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DigitalBrain.Access;

namespace DigitalBrain.Testing;

/// <summary>
/// A named person in a journey test, with their own brain and ambient caller identity.
/// </summary>
public sealed class Person
{
    private readonly INeuronRuntime _runtime;
    private readonly Actor.Person _actor;
    private readonly Caller _caller;

    /// <summary>Creates a person with <paramref name="name"/> as both account and brain id.</summary>
    public Person(INeuronRuntime runtime, string name)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _runtime = runtime;
        Name = name;
        Account = name;
        Brain = new BrainId(name);
        _actor = new Actor.Person(name, name);
        _caller = _runtime.Edge.Person(name, name, Brain);
    }

    /// <summary>The person's name.</summary>
    public string Name { get; }

    /// <summary>The person's brain.</summary>
    public BrainId Brain { get; }

    /// <summary>The person's account identifier.</summary>
    public string Account { get; }

    /// <summary>Enters an ambient execution frame stamped as this person.</summary>
    public IDisposable Enter() => _runtime.Edge.Enter(_caller);

    /// <summary>Enters an ambient execution frame stamped as an app acting on behalf of this person.</summary>
    public IDisposable EnterAsApp(string app)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        return _runtime.Edge.Enter(_runtime.Edge.App($"{Brain.Value}/apps/{app}", _actor, Brain));
    }

    /// <summary>
    /// Gets a typed proxy to <paramref name="id"/> that automatically enters this person's caller scope
    /// when invoked (preserving an already-active caller scope on this person's brain, such as <see cref="EnterAsApp"/>).
    /// </summary>
    public T Get<T>(NeuronId id) where T : class
    {
        var inner = _runtime.Get<T>(id);
        if (!typeof(T).IsInterface)
        {
            return inner;
        }
        return CallerBindingProxy.Create(inner, _runtime.Edge, _caller);
    }

    /// <summary>Opens a typed screen session in this person's brain as this person.</summary>
    public Task<AppSession<TScreen>> Open<TScreen>(string? key = null, CancellationToken ct = default)
        where TScreen : class
        => AppSession.OpenAsync<TScreen>(_runtime, Brain, _caller, key, ct);
}

/// <summary>
/// DispatchProxy wrapper used by <see cref="Person.Get{T}"/> to stamp the person's caller on each invocation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
#pragma warning disable CA1852
public class CallerBindingProxy : DispatchProxy
#pragma warning restore CA1852
{
    private object _inner = null!;
    private EdgeAuthority _edge = null!;
    private Caller _caller = null!;

    internal static T Create<T>(T inner, EdgeAuthority edge, Caller caller) where T : class
    {
        var proxy = Create<T, CallerBindingProxy>();
        var binding = (CallerBindingProxy)(object)proxy;
        binding._inner = inner;
        binding._edge = edge;
        binding._caller = caller;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.DeclaringType == typeof(object))
        {
            return targetMethod.Invoke(_inner, args);
        }

        var current = _edge.Current;
        using var scope = current is not null && current.Brain == _caller.Brain
            ? null
            : _edge.Enter(_caller);
        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
