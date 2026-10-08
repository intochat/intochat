# Settings

Settings composes a surface from app-owned preference drafts, explicit Save and Reload controls,
and credential-free connection cards. Shared brain preference fields remain authoritative.

Opening reconciles clean drafts with the stored values. Dirty drafts survive reopening. A stale
draft produces a visible conflict and must be reloaded before saving. Save captures the draft and
its durable baseline, validates a name up to 120 characters and theme system/light/dark, and writes
only edited fields with atomic expected-version writes. Concurrent updates preserve the newer value
and require reload; a partially applied multi-field save is reported explicitly. Saving, validation,
persistence failures and success appear in the surface.
Reload explicitly discards drafts. Reading returns authoritative preferences.

Connection refresh reads the brain connection registry and installed app bindings. It distinguishes
connected accounts, available connector capabilities, deployment integration configuration and
missing app configuration. Cards show provider, connection identity, status and consuming apps,
never credentials. Loading, empty and retryable error states replace the projected list. Repeated
refresh uses stable card keys and does not duplicate connections. A separate behavior owns each
of surface composition, preference persistence and connection discovery.
