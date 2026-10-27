/*
# Self-improve

The loop a developer runs by hand, as an app: a change set becomes a candidate, the candidate runs as a
sibling cluster, the current brain verifies it over MCP, and a verified candidate is promoted. The
candidates list shows every candidate, newest first, with its stage; it follows every stage change and is
the main content beside its sphere.

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

Startup and "open" compose and publish the main layout with the candidates list and report.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

## Scenario: Deployment is unavailable on this host

Startup, open and list rebuilds discover this brain's ready connector kinds before reading deployment
state. Without aspire, the list says "Self-improve is unavailable on this host because deployment is not
configured." and keeps waiting for changes. A request is refused with the same explanation and clears
the last sha; later requests are still handled. Healthy and verified callbacks cannot verify or promote
without the capability. Each action checks again, so removing deployment configuration takes effect
after an earlier successful render. Discovery and host failures remain errors.

## Scenario: A pending candidate survives temporary deployment unavailability

A candidate already followed at Healthy or Verified stays pending while deployment is unavailable.
Restoration is discovered without another candidate signal; the current stage is read again before
verifying or promoting, so an externally completed step is not repeated and the next queued candidate
can proceed. Waiting for restoration remains cancellable.

*/

using IntoChat.OS.Modules.Apps;

static class App
{
    public const string Name = "selfimprove";
    public const string Version = "2.0.9";

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
