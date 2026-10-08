# Customer Researcher

Small signal-driven scenarios compose a researcher window, submit commands and verify company details against observed pages. UI buttons and assistant tools invoke the same operations. The installed app owns its Postgres table across behavior files and upgrades.

### Show the researcher

When the open operation arrives, compose the window and return its surface.

### Submit UI commands

When Research or Stop is clicked, invoke the corresponding app operation.

### Research and store evidence

When research is requested, run the bounded browser/model algorithm and save verified evidence. Research invocations form a durable sequential queue. Stop cancels active and queued requests; result reads the stored company. The reserved tables slot binds storage through an existing Postgres connection.

Production research uses the host's default LLM. Legacy Model settings do not pin a provider.
Scratch scenario installs may supply scripted/<key> as Model to run deterministic checks.

## Scenario: Opening the researcher composes its surface

Invoking "open" answers a window whose surface is named and titled "Customer Researcher", with the
company field, the research and stop buttons, the status text and the browser paired to its driver.

## Scenario: Opening docked answers the surface without a window

Invoking "open" with the input "docked" answers the same composed "Customer Researcher" surface but
opens no window in the workspace: the shell shows it in its side panel instead.

## Scenario: Stopping an idle researcher is safe

Invoking "stop" with nothing running answers "Stop requested." and displays "Stopped" without failing.

## Scenario: Research verifies the company against observed pages and saves it

Invoking "research" with a company name drives the browser from a search page to the official
site, extracts details with the model, keeps only values found verbatim on observed official
pages, and upserts the row — keyed by the query, so a retry updates the same record. "result"
answers the stored row with its evidence.

## Scenario: An unverifiable company is refused and saves nothing

When no official page can be observed, research answers a refusal and no row is stored.

## Scenario: Stop during research cancels it or the research completes, never half-saves

A stop raced against a running research either cancels it ("Stopped.", nothing stored) or loses
the race to a completed save; there is no half-written state either way.

## Scenario: Research runs live (live)

With a connected window browser and a real model, "research" drives the visible browser and saves
findings to Postgres. Live scenarios document the app; they are not part of the deterministic gate.

## Scenario: Queued research and cancellation survive behavior recovery

Pending research invocations are admitted sequentially. UI click request IDs are preserved for replay.
Stop persists the cancelled invocation IDs before acknowledging the request, cancels the active browser
operation, and settles queued requests without starting them. Shared admission also orders terminal
status so a completed stop cannot remain displayed as "Stopping". A failing command or worker loop
cancels its sibling and exits for runtime recovery; it cannot leave a live behavior without a worker.
Shutdown leaves unfinished research pending, and deterministic query keys make retry an upsert.

## Scenario: Model output is bounded and honest

Link selection allows 2048 output tokens and extraction 4096, including model reasoning, within the
existing three-minute research deadline. Exhaustion is an explicit failed outcome, never an empty
link choice interpreted as success. Runtime diagnostics record finish reason and size, not page or
model content. Invalid or unverifiable extraction returns an error and saves no record. Unknown
contact fields remain null; every retained claim includes its observed source URL and verbatim quote.
