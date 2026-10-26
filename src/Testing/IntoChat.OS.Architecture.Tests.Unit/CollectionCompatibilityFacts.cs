using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed partial class CollectionCompatibilityFacts
{
    [Fact]
    public void PreviouslyStoredCollectionsRemainReadable()
    {
        var samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "CollectionCompatibilitySamples.json")))!;
        using var provider = BuildProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        // 06-06d: CSharpFileState is plain JSON state (clean break); its Orleans sample went. 06-08: so are AppState and its
        // AppVersionRecord (the plain app, R4-24), whose five samples went.
        Assert.Equal(40, samples.Length);
        Assert.All(samples, sample =>
        {
            var value = serializer.Deserialize<object>(Convert.FromBase64String(sample.Payload));
            Assert.NotNull(value);
            Assert.Equal(sample.Type, value.GetType().FullName);
            var property = value.GetType().GetProperty(sample.Member)!;
            Assert.Equal(sample.Expected, JsonSerializer.Serialize(property.GetValue(value), property.PropertyType, options));
            var reloaded = serializer.Deserialize<object>(serializer.SerializeToArray(value));
            Assert.Equal(sample.Expected, JsonSerializer.Serialize(property.GetValue(reloaded), property.PropertyType, options));
        });
    }

    // QUAL-03: a map stored as a dictionary reads through its view, and the next write stores the entry array and
    // drops the dictionary, so a store converts on its first write and nothing is wiped.
    [Fact]
    public void MigratedMapsReadFromOlderBytesAndConvertOnTheNextWrite()
    {
        var samples = JsonSerializer.Deserialize<Sample[]>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "CollectionCompatibilitySamples.json")))!;
        string[] migrated =
        [
            "IntoChat.OS.Modules.Flutter.Workspace.ProblemReportsState",
            "IntoChat.OS.Modules.Microsoft.CSharp.Runtime.BrainRuntimeState",
            "IntoChat.OS.Modules.Microsoft.CSharp.Runtime.ProgramAssignment",
        ];
        using var provider = BuildProvider();
        var serializer = provider.GetRequiredService<Serializer>();
        var options = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
        var covered = samples.Where(sample => migrated.Contains(sample.Type, StringComparer.Ordinal)).ToArray();
        Assert.Equal(4, covered.Length);
        Assert.All(covered, sample =>
        {
            var older = serializer.Deserialize<object>(Convert.FromBase64String(sample.Payload))!;
            var type = older.GetType();
            var property = type.GetProperty(sample.Member)!;
            var legacy = type.GetProperty("Legacy" + sample.Member);
            Assert.Equal(sample.Expected, JsonSerializer.Serialize(property.GetValue(older), property.PropertyType, options));
            if (legacy is not null) { Assert.NotNull(legacy.GetValue(older)); }

            var written = property.SetMethod is not null ? Assign(older, property) : Rebuild(older, property);
            var reloaded = serializer.Deserialize<object>(serializer.SerializeToArray(written));
            Assert.NotNull(reloaded);
            Assert.Equal(sample.Expected, JsonSerializer.Serialize(property.GetValue(reloaded), property.PropertyType, options));
            if (legacy is not null) { Assert.Null(legacy.GetValue(reloaded)); }
        });
    }

    [Fact]
    public void AnEntryArrayWinsOverTheOlderDictionary()
    {
        var state = new IntoChat.OS.Modules.Apps.AppState
        {
            LegacyFiles = new() { ["a"] = "old", ["b"] = "old" },
            FileEntries = [new("a", "new")],
        };
        Assert.Equal(new Dictionary<string, string> { ["a"] = "new" }, state.Files);
        state.PutFile("c", "added");
        Assert.Null(state.LegacyFiles);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "new", ["c"] = "added" }, state.Files);
        Assert.Equal(new Dictionary<string, string> { ["a"] = "new", ["c"] = "added" }, state.Clone().Files);
        // The export dumps state as JSON and stays a superset of what it was: the Files object is still there,
        // beside the entry array that is now what is stored.
        var exported = JsonSerializer.Serialize(state);
        Assert.Contains("\"Files\":{\"a\":\"new\",\"c\":\"added\"}", exported, StringComparison.Ordinal);
        Assert.Contains("\"FileEntries\"", exported, StringComparison.Ordinal);
    }

    private static object Assign(object value, System.Reflection.PropertyInfo property)
    {
        property.SetValue(value, property.GetValue(value));
        return value;
    }

    // Immutable records are written by constructing a new one from the view, as the neurons do.
    private static object Rebuild(object value, System.Reflection.PropertyInfo property)
    {
        var type = value.GetType();
        var constructor = type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == typeof(IEnumerable<KeyValuePair<string, string>>)));
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(IEnumerable<KeyValuePair<string, string>>)
                ? property.GetValue(value)
                : type.GetProperty(parameter.Name!, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase)!.GetValue(value))
            .ToArray();
        return constructor.Invoke(arguments);
    }

    internal static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
        {
            foreach (var assembly in ProductionArchitecture.Assemblies) { builder.AddAssembly(assembly); }
        });
        return services.BuildServiceProvider();
    }

    private sealed record Sample(string Type, string Member, string Representation, string Payload, string Expected);
}
