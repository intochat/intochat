using System.Text.Json;
using DigitalBrain.Flows;

namespace IntoChat.OS.Modules.Apps.Flows;

// A build flow's own state. Goal is the person's sentence (an app's title, spec and instruction when AppKey/Attempt name the app
// build it ends). Author fills UnitJson (the AuthoredUnit) under GoalHash; draft fills Files under UnitHash; deploy fills Bindings
// (slot -> connection key), or Missing / the choice plans while it waits, then Deployed with Hash and the install outcome.
public sealed class BuildInner
{
    public string Goal { get; set; } = "";

    public string? AppKey { get; set; }

    public Guid? Attempt { get; set; }

    public string? GoalHash { get; set; }

    public string? UnitJson { get; set; }

    public string? Alias { get; set; }

    public string? UnitHash { get; set; }

    public Dictionary<string, string>? Existing { get; set; }

    public Dictionary<string, string>? Files { get; set; }

    public Dictionary<string, string>? Bindings { get; set; }

    // A slot several connections satisfy: the candidates in the brain's Settings order. Each candidate is one plan the flow raised,
    // ChoicePlans[planId] = [slot, connection key]; the one the person approves is Chosen[slot], and the slot's other plans are withdrawn.
    public Dictionary<string, string[]>? Choices { get; set; }

    public Dictionary<string, string[]>? ChoicePlans { get; set; }

    public Dictionary<string, string>? Chosen { get; set; }

    // What deploy waits for when no connection satisfies a slot, e.g. "connect http@1 for api.x.com".
    public string? Missing { get; set; }

    public bool Deployed { get; set; }

    public string? Hash { get; set; }

    public DigitalBrain.Units.InstallStatus? Install { get; set; }

    // The installs neuron's confirmation plan while Install is Pending, and the needs sentence it asks.
    public string? PlanId { get; set; }

    public string? Sentence { get; set; }

    // The needs hash the pending ask puts to the person: the install record must carry it before the wait ends.
    public string? NeedsHash { get; set; }

    // The flow type a unit build runs as; a build flow's id is {brain}/flow.build/{key}.
    public const string Flow = "flow.build";

    // How this state is written as a flow's start payload and read back from its head (FlowHead.InnerJson).
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The unit the Author described; a build that has not been authored yet fails its draft step.
    public AuthoredUnit Unit()
        => JsonSerializer.Deserialize<AuthoredUnit>(UnitJson ?? throw new FlowFailed("draft", "the build has no authored unit"), Json)
            ?? throw new FlowFailed("draft", "the authored unit is empty");
}
