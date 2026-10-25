using System.Collections.Concurrent;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Testing.Conformance.Storage;

namespace DigitalBrain.Testing;

/// <summary>
/// A storage@1 fake for <see cref="TestOS"/> and neuron tests: the <see cref="InMemoryStorage"/> reference implementation
/// (it passes the storage conformance kit) plus a record of the <see cref="IStorage"/> members called.
/// Bind it with <see cref="TestOSStorageExtensions.WithStorage(TestOS, string, IStorage)"/>.
/// </summary>
/// <param name="label">The display label, also the root folder's name.</param>
/// <param name="quota">The quota <see cref="IStorage.Describe"/> reports; unknown when null.</param>
/// <param name="flags">The actions offered; every action by default.</param>
/// <param name="time">The clock for modification times (for example <see cref="TestOS.Clock"/>).</param>
public sealed class FakeStorage(string label = "Fake storage", Quota? quota = null, ItemFlags flags = InMemoryStorage.AllFlags, TimeProvider? time = null)
    : InMemoryStorage(label, quota, flags, null, time, "fake")
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>The <see cref="IStorage"/> members called, in order.</summary>
    public IReadOnlyList<string> Calls => [.. _calls];

    /// <inheritdoc />
    protected override void OnCall(string member) => _calls.Enqueue(member);
}

/// <summary>The storage@1 helpers over <see cref="TestOS.With{TCapability}(string, TCapability)"/>.</summary>
public static class TestOSStorageExtensions
{
    /// <summary>Binds <paramref name="storage"/> as the storage@1 connection named <paramref name="name"/>.</summary>
    public static TestOS WithStorage(this TestOS os, string name, IStorage storage)
    {
        ArgumentNullException.ThrowIfNull(os);
        return os.With(name, storage);
    }

    /// <summary>Binds a new <see cref="FakeStorage"/> (on the TestOS clock) as the storage@1 connection named <paramref name="name"/>.</summary>
    /// <param name="os">The TestOS.</param>
    /// <param name="name">The connection name.</param>
    /// <param name="label">The label; <paramref name="name"/> when null.</param>
    /// <param name="quota">The quota; unknown when null.</param>
    /// <param name="configure">Seeds or adjusts the fake before binding.</param>
    public static TestOS WithStorage(this TestOS os, string name, string? label = null, Quota? quota = null, Action<FakeStorage>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(os);
        var fake = new FakeStorage(label ?? name, quota, time: os.Clock);
        configure?.Invoke(fake);
        return os.With<IStorage>(name, fake);
    }
}
