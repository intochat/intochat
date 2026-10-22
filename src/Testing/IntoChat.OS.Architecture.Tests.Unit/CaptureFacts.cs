using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Explicit writers for the pinned ratchet baselines (RENAME-02). Each recomputes the list from the same
// source scan its ratchet fact uses and writes the baseline in place; scripts/capture-all.ps1 runs them and
// copies the results out. On an unchanged tree every writer reproduces its baseline byte for byte.
internal static class CaptureWriter
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly AsyncLocal<string?> Redirected = new();

    // Where captures write: a redirect set by PinFreshnessFacts, else INTOCHAT_CAPTURE_OUT (capture-all -Out),
    // else the committed baseline directory. Baselines are always read from the committed directory.
    internal static string Target
        => Redirected.Value
            ?? (Environment.GetEnvironmentVariable("INTOCHAT_CAPTURE_OUT") is { Length: > 0 } env ? env : Committed);

    internal static string Committed => Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory;

    internal static string Path(string file) => System.IO.Path.Combine(Committed, file);

    internal static IDisposable RedirectTo(string directory)
    {
        Redirected.Value = directory;
        return new Reset();
    }

    private sealed class Reset : IDisposable
    {
        public void Dispose() => Redirected.Value = null;
    }

    internal static void Write<T>(string file, T value)
        => File.WriteAllText(System.IO.Path.Combine(Target, file), JsonSerializer.Serialize(value, Format).ReplaceLineEndings("\n") + "\n");

    // The pinned files are not globally sorted (entries were appended by hand over time). Entries that are
    // already pinned keep their position so an unchanged tree reproduces the file byte for byte; new entries
    // follow in ordinal order. Comparison downstream is by key, never by position.
    internal static Dictionary<string, T> Sorted<T>(string file, IEnumerable<KeyValuePair<string, T>> pairs)
    {
        var position = Positions(file, document => document.RootElement.EnumerateObject().Select(property => property.Name));
        var ordered = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var pair in pairs.OrderBy(pair => position.GetValueOrDefault(pair.Key, int.MaxValue)).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ordered[pair.Key] = pair.Value;
        }
        return ordered;
    }

    internal static string[] Ordered(string file, IEnumerable<string> values)
    {
        var position = Positions(file, document => document.RootElement.EnumerateArray().Select(element => element.GetString()!));
        return values.Distinct(StringComparer.Ordinal)
            .OrderBy(value => position.GetValueOrDefault(value, int.MaxValue)).ThenBy(value => value, StringComparer.Ordinal).ToArray();
    }

    internal static string[] Ordered(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, int> Positions(string file, Func<JsonDocument, IEnumerable<string>> keys)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path(file)));
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys(document)) { position.TryAdd(key, position.Count); }
        return position;
    }
}

public sealed partial class RuntimeBoundaryFacts
{
    [Fact(Explicit = true)]
    public void CaptureGrainFactoryOwners()
        => CaptureWriter.Write("GrainFactoryOwners.json", CaptureWriter.Sorted("GrainFactoryOwners.json", CurrentGrainFactoryUse()));

    [Fact(Explicit = true)]
    public void CaptureOrleansReferenceOwners()
        => CaptureWriter.Write("OrleansReferenceOwners.json", CaptureWriter.Sorted("OrleansReferenceOwners.json", OrleansReferences()));
}

public sealed partial class ConfigurationKeyFacts
{
    [Fact(Explicit = true)]
    public void CaptureConfigurationKeyOwners()
        => CaptureWriter.Write("ConfigurationKeyOwners.json", CaptureWriter.Ordered("ConfigurationKeyOwners.json", CurrentOwners()));
}

public sealed partial class TestTierRules
{
    // The six lists live in two fact classes but one file; capture writes them together, in the file's order.
    [Fact(Explicit = true)]
    public void CaptureTestTierBaseline()
        => CaptureWriter.Write("TestTierBaseline.json", new TestTierSnapshot(
            CaptureWriter.Ordered(TestTierFacts.EndToEndReferencers()),
            CaptureWriter.Ordered(TestTierFacts.AspireReferencingModuleTests().Select(item => item.Name)),
            CaptureWriter.Ordered(TestTierFacts.FullStackBootFacts()),
            CaptureWriter.Ordered(UnitTierHosts()),
            CaptureWriter.Ordered(IntegrationWithoutFixture().Select(item => item.Name)),
            CaptureWriter.Ordered(PlaywrightReferencers())));

    private sealed record TestTierSnapshot(
        string[] LegacyEndToEndReferencers,
        string[] LegacyAspireReferencingModuleTests,
        string[] LegacyFullStackBootFacts,
        string[] LegacyUnitTierHosts,
        string[] LegacyIntegrationWithoutFixture,
        string[] LegacyPlaywrightReferencers);
}

public sealed partial class CollectionCompatibilityFacts
{
    // The payloads are stored bytes from older builds and are never rewritten. Capture refreshes only what
    // the live types say about them: the CLR type name and the expected member JSON.
    [Fact(Explicit = true)]
    public void CaptureCollectionCompatibilitySamples()
    {
        var samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(CaptureWriter.Path("CollectionCompatibilitySamples.json")))!;
        using var provider = BuildProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        var refreshed = samples.Select(sample =>
        {
            var value = serializer.Deserialize<object>(Convert.FromBase64String(sample.Payload))!;
            var property = value.GetType().GetProperty(sample.Member)!;
            return sample with
            {
                Type = value.GetType().FullName!,
                Expected = JsonSerializer.Serialize(property.GetValue(value), property.PropertyType, options),
            };
        }).ToArray();
        CaptureWriter.Write("CollectionCompatibilitySamples.json", refreshed);
    }
}

public sealed partial class PersistedStateFacts
{
    [Fact(Explicit = true)]
    public void CapturePersistedStateCollectionBaseline()
        => CaptureWriter.Write("PersistedStateCollectionBaseline.json",
            CaptureWriter.Ordered(CaptureWriter.Ordered(States.SelectMany(PersistedStateRules.ConcreteCollectionMembers))));
}
