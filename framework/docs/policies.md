# Policies

A policy is a limit a person has approved on an action a module performs. Each brain has one policies neuron.

## Actions

A module declares the actions that can be limited in its manifest (`ModuleManifest.Actions`).
An `ActionDescriptor` has a `Name` (`{module}.{verb}`), a `Description` and an optional `AmountUnit` such as `USD`.

## Rules

A `PolicyRule` limits one action:

- `Measure`: `PolicyMeasure.Count` counts calls. `PolicyMeasure.Sum` sums the amount in the action's unit.
- `Window`: a `PolicyWindow` of kind `None` (each call), `Day`, `Week`, `Month` or `Rolling` (at least one hour).
- `Limit`: the cap inside the window.
- `Condition`: an optional `PolicyCondition` on a string fact, with `PolicyOp` `Equals`, `Contains`, `StartsWith` or `EndsWith`.
- `Text`: the sentence shown to the person. `PolicyGrammar` validates a rule and renders the sentence.

## The policies neuron

`IPolicies` is the contract of the neuron at `{brain}/policies/default`. Its types are in the namespace `DigitalBrain.Limits`.

- `Propose(rule)` asks the person to confirm the rule. The result is a `ProposalOutcome` with a `ProposalStatus`: `Pending`, `Adopted`, `Denied` or `Refused`.
- `Allow(action, amount, facts)` checks every adopted rule that matches and reserves the amount. It returns a `PolicyDecision`.
- `Release(decisionId)` gives the reservation back when the external action failed.
- `Retire(ruleId)` removes a rule and its reservations.
- `List()` returns the adopted rules. `Actions()` returns the declared actions.

An action with no matching rule is allowed.

## Enforcing a decision

`PolicyGuard.Require(decision)` throws `RuleViolationException` when the decision is a refusal.
`policies.Guard(action, amount, facts, call, ct)` does the whole sequence: `Allow`, run the call, `Release` if the call throws.

## Signals

The neuron publishes `PolicyProposed`, `PolicyAdopted`, `PolicyRefused` and `PolicyRetired`.

Source: `src/DigitalBrain/Policies/`.

## The caller

Every call carries one `Caller`: an `Actor` plus brain, conversation, via and inside. Code reads it from `Context.Caller` or `ICallerAmbient`. Only `EdgeAuthority` factories build one, and the kernel is entered only through `EdgeAuthority.EnterKernel`. A call with no caller is refused on every host. Reminders, timers and activations, and their deferred work, run as `Actor.Scheduler` of the brain. A reaction runs as the caller that carried the delivery when it is of the brain, and as `Actor.Scheduler` otherwise, so `Context.Caller` is set in all neuron code a runtime runs. Inside a neuron the rule is one helper on `Neuron`: `RequireCaller()` and `RequireSameBrain()` refuse a missing caller.
