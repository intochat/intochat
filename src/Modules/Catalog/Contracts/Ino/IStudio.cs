using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Apps;

[NeuronAlias("studio", Formerly = ["ino.studio", "IntoChat.OS.Modules.Apps.IInoStudio"]), DefaultNeuronType("ino.studio")]
public interface IStudio
{
    const string DefaultKey = "ino/studio";

    // Starts the unit flow for the goal: the unit is authored, verified against its scenarios, deployed and waits for the person's
    // consent to its needs. The card points at the flow on the Tasks board; nothing is approved on the user's behalf.
    [Interleaves]
    Task<InoToolResult> Make(string goal);

    /// <summary>
    /// Authors, compiles, scenario-verifies, and deploys an interactive micro-app for <paramref name="goal"/>,
    /// optionally hinting a preferred unit <paramref name="alias"/> and <paramref name="title"/>.
    /// </summary>
    [Interleaves]
    Task<InoToolResult> CreateInteractiveApp(string? alias, string? title, string goal, CancellationToken ct = default)
        => Make(string.IsNullOrWhiteSpace(alias) ? goal : $"Create interactive app '{title ?? alias}' (alias {alias}): {goal}");

    Task<InoToolResult> Launch(string appId);

    // The studios and the deployed units, each with whether its spec.md and scenarios.json are kept.
    [ReadOnlyCall]
    Task<InoToolResult> List();

    /// <summary>
    /// Starts a hardened Feature-to-PR run on an allowlisted repository (<paramref name="repository"/>, e.g. <c>"os"</c>):
    /// prepares an isolated worktree, runs the local coder, verifies the Triple Git Lock &amp; path gate, creates 1 commit,
    /// and parks at a SHA-bound Person confirmation gate (<c>RequiresPerson = true</c>) before pushing or opening a Draft PR.
    /// </summary>
    [Interleaves]
    Task<InoToolResult> ProposeChange(string repository, string goal, CancellationToken ct = default)
        => Task.FromResult(new InoToolResult("Feature-to-PR is not available in this studio instance.", Ok: false));

    /// <summary>
    /// Starts a hardened Feature-to-PR run targeting an explicit <paramref name="baseBranch"/>.
    /// </summary>
    [Interleaves]
    Task<InoToolResult> ProposeChangeOnBranch(string repository, string goal, string baseBranch, CancellationToken ct = default)
        => Task.FromResult(new InoToolResult("Feature-to-PR is not available in this studio instance.", Ok: false));

    /// <summary>
    /// Reads the current status, commit SHA, changed files, confirmation plan ID, and Draft PR URL / pushed ref of a Feature-to-PR run.
    /// </summary>
    [ReadOnlyCall, Interleaves]
    Task<InoToolResult> ChangeStatus(string flowId, CancellationToken ct = default)
        => Task.FromResult(new InoToolResult("Feature-to-PR status is not available in this studio instance.", Ok: false));

    /// <summary>
    /// Lists the allowlisted Git repositories configured for Feature-to-PR on this host (<c>~/.intochat/repos.json</c>).
    /// </summary>
    [ReadOnlyCall, Interleaves]
    Task<InoToolResult> Repositories(CancellationToken ct = default)
        => Task.FromResult(new InoToolResult("Repository allowlist is not available in this studio instance.", Ok: false));

    /// <summary>
    /// Deterministically generates, verifies, compiles, and deploys a GitHub Issue Triage <c>Synapse</c> reaction unit
    /// bound to <paramref name="connectionKey"/> in <c>"DraftForApproval"</c> or <c>"AutoComment"</c> <paramref name="mode"/>.
    /// </summary>
    [Interleaves]
    Task<InoToolResult> CreateIssueTriageSynapse(string connectionKey, string mode = "DraftForApproval", CancellationToken ct = default)
        => Task.FromResult(new InoToolResult("Issue triage synapse installer is not available in this studio instance.", Ok: false));
}
