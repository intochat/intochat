/*
# Self-improve

The loop a developer runs by hand, as an app: a change set becomes a revision, the revision runs as a
sibling cluster, the current brain verifies it over MCP, and a verified revision is promoted. The
revisions neuron lists every revision and its stage; it is the surface the shell shows.

## Scenario: A request proposes and starts a revision

Invoking "request" with a change set (branch, summary, files, plan) proposes it through the brain's
revisions index; the last neuron holds the new sha and the revisions list shows it as Starting.

## Scenario: A healthy revision is verified with its plan

When a revision publishes RevisionHealthy, the app asks it to Verify with the plan it was proposed with.

## Scenario: A verified revision is promoted

When a revision publishes RevisionVerified, the app asks it to Promote; the list shows Promoted.

## Scenario: A failed revision shows its report and keeps its worktree

When a revision publishes RevisionFailed, the report neuron names the stage, the reason and the
worktree path; nothing is retried.

## Scenario: Opening shows the list

Invoking "open" composes a surface with the revisions list and the report, and opens a "Self-improve" window.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "selfimprove";
    public const string Version = "1.0.1";

    public static readonly AppManifest Manifest = new(
        Title: "Self-improve",
        Description: "Propose a change to the brain's own source, run it as a sibling revision, verify it, promote it.",
        Operations:
        [
            new("open", "Open the revisions list; input \"docked\" composes it for the side panel."),
            new("request", "Propose a change set: JSON with branch, summary, files and the verification plan. The answer is the revision's sha in the last neuron."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
