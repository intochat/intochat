# Compiled apps CLI

`intochat pack <project.csproj> -o <app.intochat-app>` builds an immutable package.
`intochat install <app.intochat-app>` reviews and installs it.
`intochat run <project.csproj>` builds, reviews, installs and watches a development project.

The default endpoint is `http://localhost:5080/`. Use `--endpoint <url>` and optional
`--brain <id>` to select an explicit destination. The CLI reads `/identity/session`;
when switching brains it verifies `/identity/brains`, selects `/identity/session/brain`,
and confirms the resulting session. It never extracts browser credentials.

For an authenticated endpoint intentionally provide exactly one environment variable:
`INTOCHAT_SESSION_COOKIE` containing the complete session cookie header, or
`INTOCHAT_BEARER_TOKEN` containing the bearer token. These are credentials; the CLI
sends them only to the configured endpoint and disables automatic redirects. It
never prints them. The loopback-open desktop needs neither variable.

Installation displays the digest, host-code trust warning, requirements, host
adapters and replacement status together. Type `INSTALL` in an interactive console
to approve that exact review. Redirected input or output is rejected. Compiled packages are
trusted code running with host process access, without sandbox isolation.

Development additionally requires typing `TRUST PROJECT` for future builds of the
exact project. Server leases expire after five minutes and renew once per minute.
New requirements or host adapters require a fresh package review and project trust.
Edits invalidate any build or owner review already in progress; builds run serially,
with a 150 ms debounce. Build failures preserve the installed generation. Ctrl+C
cancels builds and revokes the lease while preserving installed code and app data;
if the server is unavailable the lease still expires according to its server clock.
