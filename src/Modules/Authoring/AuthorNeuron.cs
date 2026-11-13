using DigitalBrain.Log;
using DigitalBrain.Neurons;

namespace IntoChat.OS.Modules.Authoring;

/// <summary>
/// Stateless coordinator neuron for the Authoring bounded context.
/// Validates domain authoring requests and tracks spec verification lifecycle.
/// </summary>
[Neuron(NeuronType), StatelessNeuron]
public sealed class AuthorNeuron : Neuron, IAuthorNeuron
{
    public const string NeuronType = "author";

    public Task<AuthoringSpec> PrepareSpec(AuthoringRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var alias = request.Alias ?? DeriveAlias(request.Goal);
        var scenarios = new List<AuthoringScenario>();

        if (request.ExistingFiles is not null && request.ExistingFiles.TryGetValue("scenarios.json", out var scenariosJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(scenariosJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var sc in doc.RootElement.EnumerateArray())
                    {
                        var name = sc.TryGetProperty("name", out var n) ? n.GetString() ?? "earlier" : "earlier";
                        var supersedes = sc.TryGetProperty("supersedes", out var sup) ? sup.GetString() : null;
                        var when = sc.TryGetProperty("when", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? w.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                        var then = sc.TryGetProperty("then", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Array
                            ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                        scenarios.Add(new AuthoringScenario(name, supersedes, when, then));
                    }
                }
            }
            catch (global::System.Text.Json.JsonException error)
            {
                global::Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance.LogSwallowed(error, "Invalid scenarios.json; falling back to default scenario.");
            }
        }

        if (scenarios.Count == 0)
        {
            scenarios.Add(new AuthoringScenario("default", null, [], []));
        }

        var spec = new AuthoringSpec(
            Alias: alias,
            Title: TitleFromGoal(request.Goal),
            Spec: request.Goal,
            State: new Dictionary<string, string>(),
            Signals: [],
            Needs: new AuthoringNeeds([], null, false),
            Scenarios: scenarios);

        return Task.FromResult(spec);
    }

    public Task<AuthoringVerification> VerifyBuild(AuthoringSpec spec, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(files);

        var diagnostics = new List<AuthoringDiagnostic>();
        var failures = new List<string>();

        try
        {
            spec.Validate();
        }
        catch (Exception ex)
        {
            failures.Add($"Spec validation failure: {ex.Message}");
        }

        var csFiles = files.Where(k => k.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        if (csFiles.Count == 0)
        {
            failures.Add("No C# source files present in build draft.");
        }
        else
        {
            // Verify basic syntax and multi-step [Synapse] reactive chains across C# files
            foreach (var (filename, code) in csFiles)
            {
                var openBraces = code.Count(c => c == '{');
                var closeBraces = code.Count(c => c == '}');
                if (openBraces != closeBraces)
                {
                    diagnostics.Add(new AuthoringDiagnostic(filename, "SYN001", 1, $"Unmatched braces: {openBraces} open vs {closeBraces} close."));
                    failures.Add($"Syntax error in {filename}: unmatched braces.");
                }

                // Multi-step reactive chain validation: check [Synapse] attributes
                if (code.Contains("[Synapse", StringComparison.Ordinal))
                {
                    if (!code.Contains($"\"{spec.Alias}\"", StringComparison.Ordinal) && !code.Contains($"State(\"{spec.Alias}\")", StringComparison.Ordinal))
                    {
                        diagnostics.Add(new AuthoringDiagnostic(filename, "SYN002", 1, $"[Synapse] target does not reference the unit state alias '{spec.Alias}'."));
                    }
                }
            }
        }

        // Verify preservation of previously passing scenarios when scenarios.json is present
        if (files.TryGetValue("scenarios.json", out var newScenariosJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(newScenariosJson);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    failures.Add("scenarios.json must contain a JSON array.");
                }
                else
                {
                    var newNames = new HashSet<string>(StringComparer.Ordinal);
                    var supersedingNames = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var sc in doc.RootElement.EnumerateArray())
                    {
                        if (sc.TryGetProperty("name", out var n) && n.GetString() is { } name)
                        {
                            newNames.Add(name);
                        }
                        if (sc.TryGetProperty("supersedes", out var s) && s.GetString() is { } supersedes)
                        {
                            supersedingNames.Add(supersedes);
                        }
                    }

                    // Check that previously passing scenarios from spec are preserved or superseded
                    foreach (var prior in spec.Scenarios)
                    {
                        if (prior.Name == "default") { continue; }
                        if (!newNames.Contains(prior.Name) && !supersedingNames.Contains(prior.Name))
                        {
                            failures.Add($"Previously passing scenario '{prior.Name}' was neither preserved nor superseded in refined scenarios.json.");
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                failures.Add($"scenarios.json format error: {ex.Message}");
            }
        }

        var verification = failures.Count == 0
            ? AuthoringVerification.Success(diagnostics)
            : AuthoringVerification.Failure(diagnostics, failures);

        return Task.FromResult(verification);
    }

    public Task<UnitBuildOutcome> ConcludeOutcome(AuthoringSpec spec, bool success, string? hash, IReadOnlyDictionary<string, string> files, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(files);

        var outcome = new UnitBuildOutcome(
            Success: success,
            Alias: spec.Alias,
            Hash: hash,
            Files: files,
            Status: success ? "Succeeded" : "Failed",
            Detail: detail);

        return Task.FromResult(outcome);
    }

    private static string DeriveAlias(string goal)
    {
        var cleaned = new string(goal.ToLowerInvariant().Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == ' ').ToArray());
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "unit.app",
            1 => $"unit.{parts[0]}",
            _ => $"{parts[0]}.{parts[1]}",
        };
    }

    private static string TitleFromGoal(string goal)
    {
        var trimmed = goal.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline > 0 ? trimmed[..newline].Trim() : trimmed;
    }
}
