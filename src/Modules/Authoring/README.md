# IntoChat.OS.Modules.Authoring

Unit authoring: the Creator's Author and Builder, the build flow, the synapse studio and the scenario deploy gate.

It implements contracts of `IntoChat.OS.Modules.Catalog.Contracts` and declares none of its own. It has no options of its own either: the Author and Builder models are `AuthorModel` and `BuilderModel` of the `apps` module.

Module id: `authoring`.

How to test: this module is tested in `tests/IntoChat.OS.Tests.Unit/Modules/Authoring/` and `tests/IntoChat.OS.Tests.Integration/Modules/Authoring/`.

Documentation: `docs/modules.md` at the repository root.

<!-- BEGIN GENERATED options -->
Configuration section: `IntoChatOS:Modules:authoring:Options` (environment prefix `IntoChatOS__Modules__authoring__Options__`).

Options:

None.

Secrets (names only; values are sealed per brain, never configuration):

None.

Contracts: none.
Hosting: runs in process.
<!-- END GENERATED -->
