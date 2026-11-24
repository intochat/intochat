using System.Reflection;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Testing.Synapses;
using DigitalBrain.Units;

namespace IntoChat.OS.Modules.Apps.Debugger;

// Runs a unit's kept scenarios (its scenarios.json, in the grammar verify uses) through UnitScenario on fakes, and writes the
// transcript a person reads: a line per scenario, its journaled signals and its failures. The caller bounds the time.
internal static class ScenarioTranscripts
{
    // The most scenarios one run executes.
    public const int MaxScenarios = 32;

    public static async Task<string> Run(string alias, IReadOnlyDictionary<string, string> files, string? scenario, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(files);
        var code = UnitFiles.Code(files);
        if (code.Count == 0) { return $"{alias} has no stored source."; }
        if (!files.TryGetValue(UnitFiles.Scenarios, out var json)) { return $"{alias} keeps no {UnitFiles.Scenarios}."; }
        Scenario[] scenarios;
        try { scenarios = JsonSerializer.Deserialize<Scenario[]>(json, JsonSerializerOptions.Web) ?? []; }
        catch (JsonException error) { return $"{UnitFiles.Scenarios} does not read: {error.Message}"; }
        var matching = scenarios.Where(s => string.IsNullOrWhiteSpace(scenario) || s.Name == scenario).ToArray();
        if (matching.Length == 0) { return string.IsNullOrWhiteSpace(scenario) ? $"{alias} has no scenarios." : $"{alias} has no scenario '{scenario}'."; }
        var source = new SynapseSource(alias, code, null);
        var text = new StringBuilder();
        var live = 0;
        var ran = 0;
        foreach (var each in matching)
        {
            if (each.Superseded is not null) { text.Append($"- {each.Name}: superseded\n"); continue; }
            live++;
            if (ran >= MaxScenarios) { continue; }
            ran++;
            ScenarioSteps.Parsed parsed;
            try { parsed = ScenarioSteps.Parse(each); }
            catch (FormatException error) { text.Append($"✗ {each.Name}: {error.Message}\n"); continue; }
            var transcript = await UnitScenario.Run(source, parsed.Fakes, parsed.Steps, ct).ConfigureAwait(false);
            text.Append($"{(transcript.Passed ? "✓" : "✗")} {each.Name}\n");
            foreach (var happened in transcript.Events)
            {
                // The signal's alias and its publisher, as a trace entry names them.
                var signal = happened.Signal.Signal.GetType();
                text.Append($"  step {happened.Step}: {signal.GetCustomAttribute<SignalAttribute>()?.Alias ?? signal.Name} by {happened.Signal.Publisher}\n");
            }
            foreach (var failure in transcript.Failures) { text.Append($"  failed: {failure}\n"); }
        }
        if (live > MaxScenarios) { text.Append($"(ran {MaxScenarios} of {live} scenarios)\n"); }
        return text.ToString().TrimEnd();
    }
}
