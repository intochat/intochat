using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Connections.Integrations;

namespace IntoChat.OS.Modules.Microsoft.Aspire.Candidates;

// What both candidate hosts read from the aspire Integration and how they name, check and report a candidate.
internal static partial class CandidateWorkspace
{
    public static async Task<string> Workdir(IModuleIntegrations<AspireModule> integrations, CancellationToken ct)
    {
        var snapshot = await integrations.Get("aspire").ReadAsync(ct).ConfigureAwait(false);
        return snapshot.Settings.TryGetValue(AspireModule.WorkdirField, out var workdir) && workdir is { Length: > 0 }
            ? Path.GetFullPath(workdir)
            : throw new CandidateHostException("prepare", "the aspire Integration has no Workdir");
    }

    // The aspire Integration's two secrets, projected by AspireModuleHosting from the live resource's environment.
    public static async Task<CandidateSecrets> Secrets(IModuleIntegrations<AspireModule> integrations, CancellationToken ct)
    {
        var released = await integrations.Get("aspire").ReleaseAsync(ct).ConfigureAwait(false);
        var model = released.Values.TryGetValue(AspireModule.CandidateModelField, out var json) && json is { Length: > 0 }
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []
            : [];
        return new CandidateSecrets(model);
    }

    public static string WorktreeOf(string root, string sha) => Path.Combine(root, "worktrees", sha);

    public static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Tail(ProcessResult result, int lines = 40)
    {
        var all = (result.Output + "\n" + result.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', all.TakeLast(lines));
    }

    public static void Checked(string sha)
    {
        if (sha is null || !Sha().IsMatch(sha))
        {
            throw new CandidateHostException("sha", $"'{sha}' is not a commit sha");
        }
    }

    [GeneratedRegex("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();
}
