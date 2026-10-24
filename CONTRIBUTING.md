# Contributing

- Open pull requests against `master`.
- CI must pass before a pull request is merged.
- Documentation changes go to https://github.com/intochat/os.docs (see its [contributing page](https://github.com/intochat/os.docs/blob/main/contributing.md)); product planning is internal.

Commands to run before pushing:

- `dotnet run scripts/sync-solution.cs` after adding or moving a project (`-- --check` verifies).
- `scripts/capture-all.sh` (or `scripts/capture-all.ps1`) after changing a captured surface.
- `dotnet run scripts/module-readmes.cs` after changing a module's options or secrets (`-- --check` verifies; CI runs it).

### Regenerating ratchets and pins

- One command regenerates every ratchet and pinned-list JSON (including those under `src/Testing/IntoChat.OS.Architecture.Tests.Unit/`) and the other captured surfaces: `scripts/capture-all.sh` (or `scripts/capture-all.ps1`).
- It is additive-only by default; pass `--allow-breaking` to accept removals or changes. Other flags: `--skip-sync` (skip the solution sync), `--out DIR` (write captures to DIR), `--help`.
- It runs the capture facts with `dotnet test --explicit only`; `INTOCHAT_CAPTURE_OUT` selects the output directory.
- `PinFreshnessFacts` fails CI when a pin is stale, so rerun the command and commit the result.
- Quarantined facts: timing-dependent facts are `[Fact(Explicit = true)]` with a comment linking their GitHub issue (label `flaky`). They stay out of the PR gate; run them with `dotnet test --project <proj> -c Release -- --explicit only`. Never use `Skip` or retry attributes (`NoRetryFacts`).
