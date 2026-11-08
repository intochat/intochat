using System.Text.Json;

namespace IntoChat.OS.Modules.Apps;

// What the Creator's Author produces: one synapse unit described, not yet written. The Builder writes its C# from this,
// and the unit's source carries Spec as spec.md and Scenarios as scenarios.json.
// State and signal fields are name -> C# type ("Unseen" -> "int"). Alias is lowercase and dotted ("x.watch").
public sealed record AuthoredUnit(string Alias, string Title, string Spec, IReadOnlyDictionary<string, string> State,
    IReadOnlyList<AuthoredSignal> Signals, UnitNeeds Needs, IReadOnlyList<Scenario> Scenarios);

public sealed record AuthoredSignal(string Alias, IReadOnlyDictionary<string, string> Fields);

// Schedule is an Every expression ("every 6m") or null; View is true when the unit shows a view.
public sealed record UnitNeeds(IReadOnlyList<SlotNeed> Slots, string? Schedule, bool View);

// Name is the [Slot] parameter name in the unit's code; Capability is "http" or "tool"; Hosts are what an http slot may read.
public sealed record SlotNeed(string Name, string Capability, IReadOnlyList<string> Hosts);

// Given is the slots' fakes, When the stimuli and Then the expectations, in the grammar ScenarioSteps.Parse reads.
public sealed record Scenario(string Name, JsonElement Given, IReadOnlyList<string> When, IReadOnlyList<string> Then);
