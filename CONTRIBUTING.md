# Contributing

- Open pull requests against `master`.
- CI must pass before a pull request is merged.
- Documentation changes go to https://github.com/intochat/os.docs (see its [contributing page](https://github.com/intochat/os.docs/blob/main/contributing.md)); product planning is internal.

Commands to run before pushing:

- `dotnet run scripts/sync-solution.cs` after adding or moving a project (`-- --check` verifies).
- `scripts/capture-all.sh` (or `scripts/capture-all.ps1`) after changing a captured surface.
- `dotnet run scripts/module-readmes.cs` after changing a module's options or secrets (`-- --check` verifies; CI runs it).
