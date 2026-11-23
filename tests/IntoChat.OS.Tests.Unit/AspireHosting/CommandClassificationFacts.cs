using Aspire.Hosting.ApplicationModel;
using IntoChat.Aspire;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class CommandClassificationFacts
{
    private static readonly HashSet<string> PersonOnly = new(StringComparer.Ordinal)
    {
        "reset-state",
    };

    private static readonly HashSet<string> SafeToRequest = new(StringComparer.Ordinal)
    {
        KnownResourceCommands.StartCommand,
        KnownResourceCommands.StopCommand,
        KnownResourceCommands.RestartCommand,
        "hot-reload",
        "install-model",
        "ListAllModels",
        "ListRunningModels",
    };

    [Fact]
    public void EveryCommandTheAppHostDefinesIsClassified()
    {
        var options = new AspireOptions();
        Assert.Contains("reset-state", PersonOnly);
        Assert.Equal(PersonOnly.Order(StringComparer.Ordinal), options.PersonOnlyCommands.Order(StringComparer.Ordinal));

        var os = AppHostChainFacts.Compose("local", ("IntoChatOS:Shell:Enabled", "true"));
        var defined = os.ApplicationBuilder.Resources
            .SelectMany(static r => r.Annotations.OfType<ResourceCommandAnnotation>())
            .Select(static c => c.Name)
            .Concat([KnownResourceCommands.StartCommand, KnownResourceCommands.StopCommand, KnownResourceCommands.RestartCommand])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(defined);

        var unclassified = defined.Where(name => !PersonOnly.Contains(name) && !SafeToRequest.Contains(name)).ToArray();
        Assert.True(
            unclassified.Length == 0,
            "Unclassified AppHost commands found — decide whether each belongs in AspireOptions.PersonOnlyCommands or SafeToRequest: "
            + string.Join(", ", unclassified));

        var overlap = PersonOnly.Intersect(SafeToRequest, StringComparer.Ordinal).ToArray();
        Assert.Empty(overlap);
    }
}
