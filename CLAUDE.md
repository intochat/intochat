# Repository working instructions

Public documentation lives in the sibling repository `E:/intochat/os.docs` (GitHub `intochat/os.docs`, deployed as the docs site). Product vision, requirements, plans and status live in the private handbook `E:/intochat/handbook` (GitHub `intochat/handbook`); start with its index.md and contributing.md. Do not create narrative documentation or plans in this product repository. Historical instructions and documents are archived in the documentation repository.

## Code work

- Use LSP for C# and Dart definitions, references, implementations and callers before changing code; check diagnostics after edits. Do not substitute text search for semantic change mapping.
- Use codegraph for general structure when available.
- Follow the existing neuron, signal, behavior and connector model. Propose new concepts, contract changes and module-shape changes before implementation.
- Preserve per-brain ownership and kernel-stamped signal provenance. Do not derive authority from settings.
- Contracts belong in src/IntoChat.OS and module Contracts assemblies; framework implementation belongs in Framework; platform authorities belong in Platform.
- First-party app source and executable prompts live in features/{owner}/{App}. Inline app specifications and runtime prompts are source inputs and remain here.
- Use shared module-owned testing infrastructure for engineering changes. Build individual projects on Windows rather than the solution.
- Product assessment and documentation work does not authorize running tests or starting the product. Follow the user's requested verification scope.
- For runtime behavior changes, completion requires observed behavior and telemetry as well as appropriate automated checks; do not describe source inspection as live verification.
- Keep C# wire records and Dart mirrors aligned.
- Use concrete arrays in persisted state. Avoid self-calls on non-reentrant grains. Renew observer leases.
- Never wipe user state, replace custody keys or assume stored data is disposable. Coordinate changes to persisted formats explicitly.
- Warnings are errors, nullable is enabled and package versions are central. Avoid boilerplate XML summaries; keep comments accurate.

## Documentation inputs retained here

CLAUDE.md is agent guidance. Three testing-package README files are minimal NuGet packaging pointers. Markdown under feature prompt directories is executable app input. Schema and audit utilities are tooling. All product narrative belongs in the sibling documentation repository.

