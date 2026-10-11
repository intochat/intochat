/*
# Self-improve

The loop a developer runs by hand, as an app: a change set becomes a candidate, the candidate runs as a
sibling cluster, the current brain verifies it over MCP, and a verified candidate is promoted. The
candidates list shows every candidate, newest first, with its stage; it follows every stage change and is
the surface the shell shows.

## Scenario: A request proposes and starts a candidate

Invoking "request" with a change set (branch, summary, files, plan) proposes it through the brain's
candidates index; the last neuron holds the new sha and the candidates list shows it as Starting.

## Scenario: A healthy candidate is verified with its plan

When a candidate publishes CandidateHealthy, the app asks it to Verify with the plan it was proposed with.

## Scenario: A verified candidate is promoted

When a candidate publishes CandidateVerified, the app asks it to Promote; the list shows Promoted.

## Scenario: A failed candidate shows its report and keeps its worktree

When a candidate publishes CandidateFailed, the report neuron names the stage, the reason and the
worktree path; nothing is retried.

## Scenario: A candidate that ends early does not hold up the next

A candidate that fails before it is healthy (or is discarded) ends every reaction's wait on it: the next
proposed candidate is verified and promoted as usual.

## Scenario: A malformed change set is refused

Invoking "request" with input that is not a complete change set (not JSON, or missing its branch, summary,
files or plan, or a plan whose budget is not a time span) proposes nothing: the report neuron says
"Request refused:" with the reason, the last neuron holds no sha, and the next request is handled as usual.

## Scenario: Opening shows the list

Invoking "open" composes a surface with the candidates list and the report, and opens a "Self-improve" window.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "selfimprove";
    public const string Version = "2.0.5";

    public static readonly AppManifest Manifest = new(
        Title: "Self-improve",
        Description: "Propose a change to the brain's own source, run it as a sibling candidate, verify it, promote it.",
        Operations:
        [
            new("open", "Open the candidates list; input \"docked\" composes it for the side panel."),
            new("request", "Propose a change set: JSON with branch, summary, files and the verification plan. The answer is the candidate's sha in the last neuron."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
