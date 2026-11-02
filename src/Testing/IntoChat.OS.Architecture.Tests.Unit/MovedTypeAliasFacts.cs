using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Orleans;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Types that move from the OS into DigitalBrain keep deserializing only if everything persisted carries its old name
// as [Alias] before it moves. MovedTypes.json records, per moved type, whether it reaches storage or the signal
// journal and its current alias; rewrite it from MovedTypes.Capture() when the set changes.
public sealed class MovedTypeAliasFacts
{
    [Fact]
    public void EveryPersistedMovedTypeHasAnAlias()
    {
        foreach (var entry in MovedTypes.Load().Where(e => e.Persisted))
        {
            Assert.False(string.IsNullOrEmpty(entry.Alias), $"{entry.Old} reaches storage and must carry [Alias] before it moves");
        }
    }

    [Fact]
    public void TheInventoryMatchesTheCode()
    {
        var recorded = MovedTypes.Load().OrderBy(e => e.Old, StringComparer.Ordinal).ToArray();
        Assert.Equal(MovedTypes.Capture().OrderBy(e => e.Old, StringComparer.Ordinal), recorded);
    }
}

internal sealed record Inventory(string Method, MovedTypes[] Types);

internal sealed record MovedTypes(string Old, string? Alias, bool Persisted)
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string Path => System.IO.Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "MovedTypes.json");

    // Top-level types declared in the files that move (spec section 4.1), by CLR full name.
    private static readonly string[] Names =
    [
        "IntoChat.OS.Framework.ISignalJournal", "IntoChat.OS.Framework.PartSignals", "IntoChat.OS.Framework.JournaledSignal",
        "IntoChat.OS.Framework.SignalJournal", "IntoChat.OS.Framework.MemorySignalJournal", "IntoChat.OS.Framework.MemorySignalJournalOptions",
        "IntoChat.OS.Framework.BrainAffinePlacement", "IntoChat.OS.Framework.BrainAffinePlacementAttribute", "IntoChat.OS.Framework.BrainAffinePlacementDirector",
        "IntoChat.OS.Framework.NeutralCallMapping", "IntoChat.OS.Framework.NeutralCallFilter", "IntoChat.OS.Framework.TaskBoxing",
        "IntoChat.OS.Framework.IRevisioned`1",
        "IntoChat.OS.Framework.Modules.OsModule",
        "IntoChat.OS.NeuronAliases", "IntoChat.OS.Runtime.NeuronProxies",
        "IntoChat.OS.State", "IntoChat.OS.Trace",
    ];

    internal static MovedTypes[] Load() => JsonSerializer.Deserialize<Inventory>(File.ReadAllText(Path), Format)!.Types;

    // Persisted: [GenerateSerializer] (itself or a nested type), a field, property or array element type of a
    // [GenerateSerializer] type in the production assemblies, or a DigitalBrain.Signal.
    internal static MovedTypes[] Capture()
    {
        var all = ProductionArchitecture.Types;
        var referenced = all.Where(t => t.IsDefined(typeof(GenerateSerializerAttribute), inherit: false))
            .SelectMany(t => t.GetFields(Members).Select(f => f.FieldType).Concat(t.GetProperties(Members).Select(p => p.PropertyType)))
            .SelectMany(Unwrap).ToHashSet();
        return Names.Select(name =>
        {
            var type = all.Single(t => t.FullName == name);
            var persisted = type.IsDefined(typeof(GenerateSerializerAttribute), inherit: false)
                || type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Any(n => n.IsDefined(typeof(GenerateSerializerAttribute), inherit: false))
                || referenced.Contains(type)
                || typeof(DigitalBrain.Signal).IsAssignableFrom(type);
            return new MovedTypes(name, type.GetCustomAttribute<AliasAttribute>(inherit: false)?.Alias, persisted);
        }).ToArray();
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (type.HasElementType) { foreach (var inner in Unwrap(type.GetElementType()!)) { yield return inner; } }
        if (type.IsGenericType) { foreach (var inner in type.GetGenericArguments().SelectMany(Unwrap)) { yield return inner; } }
    }
}
