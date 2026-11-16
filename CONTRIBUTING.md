# Contributing

- Open pull requests against `master`.
- CI must pass before a pull request is merged.
- Documentation lives next to the code: `docs/` for the product and `framework/docs/` for DigitalBrain. A change that makes a page untrue fixes the page in the same pull request. Dated plans and reports do not go in `docs/`.

Commands to run before pushing:

- `dotnet run scripts/sync-solution.cs` after adding or moving a project (`-- --check` verifies).
- `dotnet run scripts/module-readmes.cs` after changing a module's options or secrets (`-- --check` verifies; CI runs it).
- When a grain, contract, state, or signal identity changes, rewrite `src/Testing/IntoChat.OS.Architecture.Tests.Unit/ContractManifest.json` from `ContractManifest.Capture()` and commit it with the change. `ContractManifestFacts` fails when that file drifts.

### Ratchets

- The architecture project keeps boundary facts (framework inwardness, the Orleans reach ban, test tiers, repository layout, namespace scheme, swallow). There is no capture-all script and no pin JSON besides `ContractManifest.json`.
- Quarantined facts: timing-dependent facts are `[Fact(Explicit = true)]` with a comment linking their GitHub issue (label `flaky`). They stay out of the PR gate; run them with `dotnet test --project <proj> -c Release -- --explicit only`. Never use `Skip` or retry attributes (`NoRetryFacts`).

### New in, old out

A change that introduces a new way of doing something removes the old way in the same pull request. If the old way cannot be removed yet, the pull request adds it to a tracked list with an owner and is not called done.
