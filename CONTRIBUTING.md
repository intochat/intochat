# Contributing

- Open pull requests against `master`.
- CI must pass before a pull request is merged.
- Documentation lives next to the code: `docs/` for the product and `framework/docs/` for DigitalBrain. A change that makes a page untrue fixes the page in the same pull request. Dated plans and reports do not go in `docs/`.

## Where things go

- The framework is in `framework/`. It may not reference anything named `IntoChat` (`FrameworkBoundaryFacts`).
- System apps are in `src/IntoChat.OS.Apps`, one folder per app with its scenarios.
- Modules are in `src/Modules`, one folder per module.
- The Flutter shell is in `ui`. No project files for .NET go there.
- Apps never receive a credential. They reach outside services through `ICapabilityBroker` and a connection.

## Checks to run before pushing

CI runs these (`.github/workflows/ci.yml`). On Windows, build single projects, not the whole solution.

```
dotnet format whitespace Intochat.OS.slnx --verify-no-changes
dotnet build <project> -c Release
dotnet test --project <test project> -c Release
```

For the shell, from `ui`:

```
flutter pub get --enforce-lockfile
dart format --output=none --set-exit-if-changed lib test
dart analyze --fatal-infos
flutter test --exclude-tags golden
```

Test projects are in `tests` and `framework/tests`. The project name ends with its tier: Unit, Integration, E2E or Browser. [Testing tiers](framework/docs/testing-tiers.md) describes them.

When a grain, contract, state, or signal identity changes, rewrite `tests/IntoChat.OS.Architecture.Tests.Unit/ContractManifest.json` from `ContractManifest.Capture()` and commit it with the change. `ContractManifestFacts` fails when that file drifts.

### Ratchets

- The architecture project keeps boundary facts (framework inwardness, the Orleans reach ban, test tiers, repository layout, namespace scheme, swallow). There is no capture-all script and no pin JSON besides `ContractManifest.json`.
- Quarantined facts: timing-dependent facts are `[Fact(Explicit = true)]` with a comment linking their GitHub issue (label `flaky`). They stay out of the PR gate; run them with `dotnet test --project <proj> -c Release -- --explicit only`. Never use `Skip` or retry attributes (`NoRetryFacts`).

### New in, old out

A change that introduces a new way of doing something removes the old way in the same pull request. If the old way cannot be removed yet, the pull request adds it to a tracked list with an owner and is not called done.
