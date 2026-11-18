using System.Text.Json;
using System.Text.Json.Serialization;

namespace DigitalBrain.Testing.Scenarios;

/// <summary>A scripted walk through one app's top-level screen that any driver (kernel, Flutter, browser) can run.</summary>
/// <param name="Name">The scenario name, unique in its set.</param>
/// <param name="AppId">The app whose top-level screen every step addresses.</param>
/// <param name="Steps">The steps, in order.</param>
public sealed record UiScenario(string Name, string AppId, IReadOnlyList<ScenarioStep> Steps);

/// <summary>One step of a <see cref="UiScenario"/>. The target is a key path within the app's top-level screen: "." is the screen
/// itself, "nav" or "conversation/composer" a slot child; raw child neuron ids are never used (the edge refuses them).</summary>
/// <param name="Target">The key path within the app's top-level screen.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "step")]
[JsonDerivedType(typeof(Act), "act")]
[JsonDerivedType(typeof(Expect), "expect")]
public abstract record ScenarioStep(string Target);

/// <summary>Sends a command to the screen, routed by key path: what a user's click or keystroke sends.</summary>
/// <param name="Target">The key path within the app's top-level screen ("." is the screen itself).</param>
/// <param name="Signal">The command's signal alias.</param>
/// <param name="Args">The command's args, or none.</param>
public sealed record Act(string Target, string Signal, JsonElement? Args = null) : ScenarioStep(Target);

/// <summary>Reads what the target shows and expects it to contain a text.</summary>
/// <param name="Target">The key path within the app's top-level screen ("." is the whole screen).</param>
/// <param name="Contains">The text the target must show.</param>
public sealed record Expect(string Target, string Contains) : ScenarioStep(Target);

/// <summary>How a run ended.</summary>
/// <param name="Passed">True when every step held.</param>
/// <param name="FailedStep">The index of the step that failed, or none.</param>
/// <param name="Reason">Why it failed, or none.</param>
/// <param name="EvidenceRef">The driver's evidence for the failing step (a screenshot path, a view dump), or none.</param>
public sealed record RunVerdict(bool Passed, int? FailedStep = null, string? Reason = null, string? EvidenceRef = null);

/// <summary>Runs scenario steps against one surface: the kernel, the Flutter client or a browser.</summary>
public interface IScenarioDriver
{
    /// <summary>The driver name a run records (kernel, flutter, browser).</summary>
    string Name { get; }

    /// <summary>Sends the step's command to the app's top-level screen.</summary>
    /// <param name="step">The step.</param>
    /// <param name="ct">Cancellation.</param>
    Task Act(Act step, CancellationToken ct);

    /// <summary>The text the target currently shows.</summary>
    /// <param name="target">The key path within the app's top-level screen.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string> Read(string target, CancellationToken ct);

    /// <summary>Captures evidence of the surface at a step (a screenshot path, a view dump), or none.</summary>
    /// <param name="stepIndex">The step index.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string?> Evidence(int stepIndex, CancellationToken ct);
}

/// <summary>Scenario sets as JSON, the shape every driver reads (the Flutter driver reads the exported file).</summary>
public static class ScenarioJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Web) { WriteIndented = true };

    /// <summary>Serialises scenarios, indented, with LF line endings and a trailing newline.</summary>
    /// <param name="scenarios">The scenarios.</param>
    public static string Write(IReadOnlyList<UiScenario> scenarios) => JsonSerializer.Serialize(scenarios, Options).ReplaceLineEndings("\n") + "\n";

    /// <summary>Reads scenarios written by <see cref="Write"/>.</summary>
    /// <param name="json">The JSON.</param>
    public static IReadOnlyList<UiScenario> Read(string json) => JsonSerializer.Deserialize<UiScenario[]>(json, Options) ?? [];
}
