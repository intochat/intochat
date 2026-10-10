# Aspire instance neuron

**Status:** approved in chat 2026-10-10. Sub-project A of 3 (A: instance neuron, B: Engineer app,
C: Twitter/price modules + "How Musk Moves Bitcoin" demo). Supersedes the Launcher, Revisions module
and Aspire bridge parts of `2026-10-09-self-improving-loop-design.md`; that spec's intent, the
verification plan and the SelfImprove app stay.

## Intent

One concept for "a running DigitalBrain": an Aspire neuron is one running instance. The brain
improves itself by proposing a new instance from changed source, verifying it from the current one
over MCP, and handing over. Today that concept is built three times (the Aspire bridge into the
current AppHost, the Launcher process, the Revisions module) and none of them is a neuron.

## Neuron

`IAspire` keyed `aspire/{sha}`. The current instance is one of them; there is no special neuron.

State:

```
Revision  sha, branch, worktree path
Ports     ui, mcp, dashboard (allocated block)
Cluster   serviceId/clusterId = intochat-r{short-sha}, storage container brain-r{short-sha}
Stage     Proposed → Starting → Healthy → Verified → Current → Stopped | Failed(stage, reason)
Plan      VerificationPlan (resources healthy, neuron calls + expected state, span names, budget)
```

Contract:

- `Propose(branch, plan)`: creates the worktree from the current revision.
- `Start()`: the connector copies storage and spawns `aspire run` detached in the worktree with the
  cluster and port keys; publishes `AspireStarted(endpoints)`.
- `Verify()`: drives the child through its `digitalbrain-mcp` and its Aspire dashboard MCP, both via
  `McpServerNeuron`; publishes `AspireVerified(report)` or `AspireFailed(stage, reason)`.
- `Promote()`: refused without `Verified` in history. Commits the worktree to the branch, rewrites
  the current pointer, publishes `AspirePromoted`, then calls `RequestStop` on the child.
- `RequestStop()`: called on the new current by the old one; the new current stops the caller's
  process group through its own `aspire/{old}` neuron. An instance never stops itself.
- `Stop()` / `Discard()`: stop the process; `Discard` keeps the worktree.
- `Resources()`: the instance's resources, read through its dashboard MCP.

At most one candidate per brain; a second `Propose` while one is live queues.

## Connector

`AspireProcessConnector` is the only code touching git, processes, ports and Azurite. It requires
the deployment Integration `DigitalBrain:Integrations:aspire:Workdir`; without it `Start` fails with
`Failed(Start, "no aspire integration")`. Processes are spawned in their own process group so a
child outlives its parent. A cloud connector is out of scope.

## Current pointer

Each revision has its own storage copy, so grain state cannot name the current instance across
clusters. The pointer is the file `{Workdir}/current.json` (sha, ports), written only by the
connector. The shell, MCP clients and the repo-root `digitalbrain.cs` resolve it at connect time and
reconnect when the instance drops. Promote costs a few seconds of reconnect; there is no proxy.

## Handover

1. Parent's `aspire/{child}` is `Verified`; `Promote()` commits the worktree.
2. Connector rewrites `current.json` to the child.
3. Parent publishes `AspirePromoted`, calls the child's `RequestStop`.
4. Child stops the parent's process group; clients reconnect via `current.json`.

Writes the parent takes after the child's `Start` do not carry over (storage was copied at Start).
Accepted for the local dev loop; no sync, no migrations.

## Failures

| Where | Outcome |
|---|---|
| Worktree, storage copy or spawn | `Failed(Start)`, worktree kept |
| Not healthy within the plan budget | `Failed(Healthy)`, process stopped |
| Plan expectation missed | `Failed(Verify, expectation)`, child stopped, worktree kept |
| Child dies after promote, before stopping the parent | On activation the connector reconciles `current.json` against live processes and points it back at the running one |
| Orphan processes after a host crash | On activation the connector stops process groups no `aspire/*` neuron owns |

No retries, no raised timeouts: every failure is a terminal stage with a reason.

## Deleted

- `src/DigitalBrain.Launcher`
- `src/DigitalBrain.Modules/DigitalBrain/Revisions` (`IRevision`, `IRevisions`, `McpNeuronClient`,
  `HttpLauncherClient`)
- The Aspire bridge: `AspireBridge`, `AspireBridgeClient`, the dial-back stream and its routes
- `features/os/SelfImprove` synapses move from revision signals to the `IAspire` signals

## Testing

- Module facts, `ModuleTest.Create().WithModule<AspireModule>()` with a `FakeAspireProcess`
  connector: the stage machine; `Promote` refused without `Verified`; `current.json` reconciled
  against live processes; orphan process groups stopped; a missed plan expectation yields
  `Failed(Verify)` naming it.
- Hosting fact: service id, cluster id and storage container come from configuration with the
  `intochat` defaults.
- Running: ask SelfImprove for a one-line Calculator change; `intochat-r…` resources come up;
  traces show `AspireVerified` then `AspirePromoted`; the shell reconnects; the old AppHost exits.

## Out of scope

Cloud connector, zero-downtime handover, replicas of one revision, rollback after promotion
(git revert is the rollback), sub-projects B and C.
