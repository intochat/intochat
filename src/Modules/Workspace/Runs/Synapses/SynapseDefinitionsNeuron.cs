using DigitalBrain.Log;
using IntoChat.OS.Modules.AI.Workflows;
using IntoChat.OS.Modules.Apps.AppRuns;
using IntoChat.OS.Modules.Apps.Runs;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps.Synapses;

// Reads the brain's behavior from where each form lives: deployed units from the synapse studio, workflows from the host's
// definitions, flows and agents from the runs they made. Keeps nothing; each read is current.
[DigitalBrain.Neurons.Neuron("synapse.definitions")]
internal sealed class SynapseDefinitionsNeuron(IEnumerable<WorkflowDefinition> workflows) : DigitalBrain.Neurons.Neuron, ISynapseDefinitions
{
    public async Task<SynapseDefinition[]> List()
    {
        var runs = await Optional(() => Context.Get<IRuns>(IRuns.Key(Id.Brain.Value)).List(200)) ?? [];
        var units = await Optional(() => Context.Get<ISynapseStudio>(new DigitalBrain.NeuronId(Id.Brain, "ino.synapses", "ino/synapses")).List()) ?? [];
        var apps = await Optional(() => Context.Get<IDeployedApps>(IDeployedApps.Key(Id.Brain.Value)).List()) ?? [];
        return SynapseDefinitions.Of(
            [.. units.SelectMany(unit => unit.Aliases)],
            [.. workflows.Select(workflow => (workflow.Name, workflow.Description))],
            runs,
            apps);
    }

    // A form whose source is not hosted in this brain simply contributes nothing.
    private static async Task<T?> Optional<T>(Func<Task<T>> read) where T : class
    {
        try { return await read(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<SynapseDefinitionsNeuron>().LogSwallowed(error, "A synapse source is not hosted in this brain; its form is left out.");
            return null;
        }
    }
}

internal static class SynapseDefinitions
{
    internal static SynapseDefinition[] Of(string[] unitAliases, (string Name, string Description)[] workflows, RunView[] runs, DeployedApp[]? apps = null)
    {
        SynapseDefinition With(SynapseForm form, string name, string description, RunKind kind)
        {
            var own = runs.Where(run => run.Kind == kind && string.Equals(run.Synapse, name, StringComparison.Ordinal)).ToArray();
            var last = own.OrderByDescending(run => run.Started).FirstOrDefault();
            return new($"{form.ToString().ToLowerInvariant()}:{name}", form, name, description, own.Length, last?.State.ToString());
        }

        var known = new List<SynapseDefinition>();
        known.AddRange(unitAliases.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(alias => With(SynapseForm.Reaction, alias, "A reaction unit.", RunKind.Operation)));
        known.AddRange(workflows.Select(workflow => With(SynapseForm.Workflow, workflow.Name, workflow.Description, RunKind.Workflow)));
        known.AddRange(Seen(runs, RunKind.Flow).Select(type => With(SynapseForm.Flow, type, "A durable flow.", RunKind.Flow)));
        known.AddRange(Seen(runs, RunKind.AgentTurn).Select(app => With(SynapseForm.Agent, app, "An agent.", RunKind.AgentTurn)));
        var deployed = apps ?? [];
        known.AddRange(deployed.Select(app => With(SynapseForm.App, app.App, Describe(app), RunKind.App)));
        known.AddRange(Seen(runs, RunKind.App).Where(name => deployed.All(app => app.App != name))
            .Select(app => With(SynapseForm.App, app, "An installed sandbox app.", RunKind.App)));
        known.AddRange(Seen(runs, RunKind.CodeBlock).Select(name => With(SynapseForm.CodeBlock, name, "Generated C# run once in the sandbox.", RunKind.CodeBlock)));
        return [.. known];
    }

    private static string Describe(DeployedApp app)
    {
        var parts = new List<string>();
        if (app.Commands.Length > 0) { parts.Add("commands: " + string.Join(", ", app.Commands)); }
        if (app.Schedules.Length > 0) { parts.Add("every " + string.Join(", ", app.Schedules)); }
        return parts.Count > 0 ? "An installed sandbox app; " + string.Join("; ", parts) + "." : "An installed sandbox app.";
    }

    private static IEnumerable<string> Seen(RunView[] runs, RunKind kind)
        => runs.Where(run => run.Kind == kind && run.Synapse is { Length: > 0 }).Select(run => run.Synapse!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
}
