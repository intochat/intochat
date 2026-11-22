using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Testing;

/// <summary>
/// A collection of edge fakes registered once and applied to both TestOS and Silo service collections.
/// </summary>
public sealed class FakeSet
{
    private readonly List<(Type EdgeType, object Instance)> _registrations = [];
    private readonly Dictionary<Type, object> _byFakeType = [];

    /// <summary>Registers <paramref name="fake"/> as the singleton implementation of <typeparamref name="TEdge"/>.</summary>
    public FakeSet Add<TEdge, TFake>(TFake fake)
        where TEdge : class
        where TFake : class, TEdge
    {
        ArgumentNullException.ThrowIfNull(fake);
        _registrations.Add((typeof(TEdge), fake));
        _byFakeType[typeof(TFake)] = fake;
        return this;
    }

    /// <summary>Returns the registered fake of type <typeparamref name="TFake"/>, or throws if none was added.</summary>
    /// <exception cref="InvalidOperationException">No fake of type <typeparamref name="TFake"/> was added.</exception>
    public TFake Get<TFake>() where TFake : class
    {
        if (_byFakeType.TryGetValue(typeof(TFake), out var instance))
        {
            return (TFake)instance;
        }
        throw new InvalidOperationException($"No fake of type '{typeof(TFake).FullName ?? typeof(TFake).Name}' was added to this FakeSet.");
    }

    /// <summary>Replaces any existing registrations for each added edge type with its fake singleton.</summary>
    public void Apply(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var (edgeType, instance) in _registrations)
        {
            for (var index = services.Count - 1; index >= 0; index--)
            {
                if (services[index].ServiceType == edgeType)
                {
                    services.RemoveAt(index);
                }
            }
            services.Add(ServiceDescriptor.Singleton(edgeType, instance));
        }
    }
}
