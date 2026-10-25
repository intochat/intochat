using System.Text.Json;

namespace IntoChat.OS.Architecture.Tests.Unit;

// "Synapse" has one meaning (CORE-03, D-15): the durable routing of a signal kind from one neuron to another.
// A script file with one signal loop is a reaction (ReactionShape, RXN0001/RXN0002). Every production type whose
// name contains "Synapse" is listed in SynapseWordAllowlist.json; a new one fails here. The list only shrinks:
// the remaining app-contract names (AppSynapse, SynapseStatus, SynapseWritten, SynapseRejected, SynapseBody)
// are persisted or wire contracts that still say synapse for a written reaction and leave the list when their
// coordinated rename lands.
public sealed class SynapseWordFacts
{
    internal const string FileName = "SynapseWordAllowlist.json";

    internal static string[] CurrentSynapseTypes()
        => CaptureWriter.Ordered(ProductionArchitecture.Types
            .Where(type => !type.Assembly.GetName().Name!.Contains(".Tests", StringComparison.Ordinal))
            // The DigitalBrain core owns the Synapse word (its own repository's facts govern it); the ratchet covers os code.
            .Where(type => !type.Assembly.GetName().Name!.StartsWith("DigitalBrain", StringComparison.Ordinal))
            .Where(type => type.Name.Contains("Synapse", StringComparison.Ordinal))
            .Select(type => type.FullName!.Replace('+', '.')));

    [Fact]
    public void EverySynapseNamedTypeIsAllowlisted()
    {
        var allowed = JsonSerializer.Deserialize<string[]>(File.ReadAllText(CaptureWriter.Path(FileName)))!.ToHashSet(StringComparer.Ordinal);
        var current = CurrentSynapseTypes();

        var added = current.Where(name => !allowed.Contains(name)).ToArray();
        Assert.True(added.Length == 0,
            "synapse means durable routing only; use reaction for script shape:\n" + string.Join('\n', added));

        var gone = allowed.Where(name => !current.Contains(name, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(gone.Length == 0, $"These types are gone; shrink {FileName} so the list holds:\n" + string.Join('\n', gone));
    }

    [Fact]
    public void NoPromptSaysSynapseShape()
    {
        var features = Path.Combine(Repository.Root, "features");
        var offenders = Directory.EnumerateFiles(features, "*", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("synapse shape", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(Repository.Root, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0,
            "synapse means durable routing only; use reaction for script shape:\n" + string.Join('\n', offenders));
    }

    [Fact(Explicit = true)]
    public void CaptureSynapseWordAllowlist()
        => CaptureWriter.Write(FileName, CurrentSynapseTypes());
}
