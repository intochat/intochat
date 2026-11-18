# Repository working instructions

Documentation lives next to the code: `docs/` for the product and `framework/docs/` for DigitalBrain. A change that makes a page untrue fixes the page in the same pull request. Dated plans, audits and reports do not go in this repository; they belong in the owner's planning folder (`E:/intochat/cloud/web/docs/plans`) and move to `E:/intochat/archive` when the work ends.

## Code work

- Use LSP for C# and Dart definitions, references, implementations and callers before changing code; check diagnostics after edits. Do not substitute text search for semantic change mapping.
- Use codegraph for general structure when available.
- Follow the existing neuron, signal, behavior and connector model. Propose new concepts, contract changes and module-shape changes before implementation.
- Preserve per-brain ownership and kernel-stamped signal provenance. Do not derive authority from settings.
- Contracts belong in `src/IntoChat.OS` and module `Contracts` assemblies (including `src/Modules/Shell/Contracts`, `IntoChat.OS.Modules.Shell.Contracts`); generic runtime implementation belongs in DigitalBrain (`framework/src/*`); OS-only hosting belongs in `src/IntoChat.OS.Framework`; platform authorities belong in `src/IntoChat.OS.Framework/Authority` (`IdentityModule`).
- First-party apps are compiled shell apps in `src/IntoChat.OS.Apps/<App>`, one folder per app with its scenarios; apps authored at runtime are synapse units, never source in this repository. Inline app specifications and runtime prompts are source inputs and remain here.
- Use shared module-owned testing infrastructure for engineering changes. Build individual projects on Windows rather than the solution. Tiers prove one subject: Unit = facts on TestOS, Integration = the same on a real silo through Testing.Silo, E2E = the product on an in-process host without Docker, Browser = the shell in a browser (nightly). The product tier boots the OS as a DigitalBrain app under Aspire's testing builder with no container; a journey that needs a container carries a `needs` trait and runs nightly. CI enforces a time budget per tier.
- Product assessment and documentation work does not authorize running tests or starting the product. Follow the user's requested verification scope.
- For runtime behavior changes, completion requires observed behavior and telemetry as well as appropriate automated checks; do not describe source inspection as live verification.
- Keep C# wire records and Dart mirrors aligned.
- Use concrete arrays in persisted state. Avoid self-calls on non-reentrant grains. Renew observer leases.
- Never wipe user state, replace custody keys or assume stored data is disposable. Coordinate changes to persisted formats explicitly.
- DigitalBrain lives under `framework/` in this repository. Product projects reference `framework/src/*` directly with no version pins; `framework/` may never reference anything named `IntoChat` (`FrameworkBoundaryFacts`).
- Warnings are errors, nullable is enabled and package versions are central. Avoid boilerplate XML summaries; keep comments accurate.

## New in, old out

A change that introduces a new way of doing something removes the old way in the same pull request. If the old way cannot be removed yet, the pull request adds it to a tracked list with an owner and is not called done.

## Documentation rules

`docs/` holds at most: index, getting started, concepts, architecture, and one page per major part. Every type name and file path a page puts in backticks must exist in the code. The root README.md and each module README.md stay short, with the options and secrets tables kept in step by `ModuleDocumentationFacts` between its markers (run it with `UPDATE_DOCS=1` to rewrite them). Markdown under feature prompt directories is executable app input, not documentation.
