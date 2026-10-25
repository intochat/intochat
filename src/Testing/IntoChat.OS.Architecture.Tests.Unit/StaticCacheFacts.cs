using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A process-wide mutable dictionary is a leak unless its keys are a closed set. These are keyed by Type, MethodInfo or a type
// name, so they hold at most the code the process loaded. Content-keyed caches belong in a DI-owned BoundedCache, not a static.
// The owner list is closed: a new static dictionary fails until it is justified here, and an owner that disappears fails too.
public sealed partial class StaticCacheFacts
{
    private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal)
    {
        ["CallMarkers.Methods"] = "MethodInfo",
        ["LoadedContracts.Found"] = "string",
        ["Neuron.OneWayTargets"] = "MethodInfo",
        ["LegacyGrainSink.Contracts"] = "string",
        ["LegacyGrainSink.HostTypes"] = "string",
        ["NeuronCallFilter.Policies"] = "(Type",
        ["NeuronProxy.Casts"] = "Type",
        ["NeutralCallMapping.Methods"] = "MethodInfo",
        ["NeutralCallMapping.Predicates"] = "Type",
        ["PlainNeuronCallFilter.Policies"] = "(string Contract",
        ["PlainNeurons.Factories"] = "Type",
        ["SignalTypes.Loaded"] = "string",
        ["TaskBoxing.Boxes"] = "Type",
    };

    [GeneratedRegex(@"\bstatic\s+(?:readonly\s+)?(?:System\.Collections\.Concurrent\.)?(?:Concurrent)?Dictionary<(?<key>[^,<>]+(?:<[^>]*>)?|\([^)]*\)),[^=;]*>\s+(?<name>\w+)\s*(?:=|;)", RegexOptions.CultureInvariant)]
    private static partial Regex StaticDictionary();

    [Fact]
    public void StaticDictionariesAreAClosedListKeyedByTypesMethodsOrTypeNames()
    {
        var found = Repository.ProjectFiles("*.cs", null,
                project => !project.Name.EndsWith(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Tests.", StringComparison.Ordinal)
                    && !project.Name.EndsWith(".Testing", StringComparison.Ordinal))
            .SelectMany(path => StaticDictionary().Matches(File.ReadAllText(path))
                .Select(match => (Owner: Path.GetFileNameWithoutExtension(path) + "." + match.Groups["name"].Value, Key: match.Groups["key"].Value.Trim())))
            .ToDictionary(entry => entry.Owner, entry => entry.Key, StringComparer.Ordinal);

        Assert.Equal(Owners.Keys.Order(StringComparer.Ordinal), found.Keys.Order(StringComparer.Ordinal));
        foreach (var (owner, key) in found)
        {
            Assert.True(key.StartsWith(Owners[owner], StringComparison.Ordinal), $"{owner} is keyed by {key}; expected {Owners[owner]}. Open-ended keys need a BoundedCache.");
        }
    }
}
