/*
# Customer Researcher

Researches companies in a driven browser and saves only verified findings to the app's own table.
The Research and Stop buttons post the same operations the assistant's tools do.

### Show the researcher

Startup and "open" compose and publish the researcher main layout: the company field,
Research and Stop buttons, status, activity list and browser paired to its driver.

### Queue research

"research" appends the company to a durable queue. One worker researches the queue in order, one company
at a time, and records each outcome. The queue neuron shows every request with its state; the status
neuron shows the current step or the last outcome. Research and stop arrive on one ordered request stream,
so a stop cancels every research requested before it. Each fact of the queue has exactly one writer:
requests and cancellations (the commands synapse), outcomes and the active marker (the worker).

### Research and store evidence

Research drives the browser from a search page to the official site, asks the host's default LLM for a
bounded link choice and then an extraction, and keeps only values found verbatim on observed official
pages, each with its source URL and quote. The row is upserted into the app's table, keyed by the query.
"result" reads the stored row for a company into {app}/result/{requestId}, or null. Legacy Model settings
do not pin a provider.

### Stop

"stop" cancels every open request: waiting ones never start and the running one stops at its next browser
or model step, saving nothing. Saved records stay.

### Storage

The table lives where the platform places it: in the Postgres connection bound to the reserved tables
slot or, with none bound, in the deployment's default database. Researching needs no configuration.

## Scenario: Opening the researcher composes its surface

Invoking "open" rebuilds the published main layout with stable neuron identities.

## Scenario: Stopping an idle researcher is safe

Invoking "stop" with nothing running sets the status neuron to "Stopped" without failing; the queue and the
saved records are unchanged.

## Scenario: Research verifies the company against observed pages and saves it

Invoking "research" with a company name drives the browser from a search page to the official
site, extracts details with the model, keeps only values found verbatim on observed official
pages, and upserts the row — keyed by the query, so a retry updates the same record. The request settles
as "Saved: {company}" and "result" sets its result neuron to the stored row with its evidence.

## Scenario: An unverifiable company is refused and saves nothing

When no official page can be observed (no connected browser, no readable page, no verifiable identity),
the request settles as failed with the reason in the status and the queue, and "result" for that company
stays null.

## Scenario: Stop during research cancels it or the research completes, never half-saves

A stop raced against a running research either cancels it at its next step ("Stopped", nothing stored) or
loses the race to a completed save; there is no half-written state either way. Every research requested
before the stop that has not started settles as stopped without starting, even when the requests and the stop
arrive back to back.

## Scenario: Research runs live (live)

With a connected window browser and a real model, "research" drives the visible browser and saves
findings to Postgres. Live scenarios document the app; they are not part of the deterministic gate.

## Scenario: Queued research and cancellation survive behavior recovery

The queue is neuron state, never process memory. A request delivered twice is queued once, a stop delivered
twice is applied once, and a click delivered twice is posted once. Stop persists its cancellations before it
shows "Stopping…" or "Stopped", and a run that completes meanwhile is shown with its outcome, never left as
"Stopping…". When the worker restarts it resumes the first unfinished request; a run that was stopped while
the worker was down settles as stopped, and a settle a restart interrupted (outcome recorded, status not yet
shown) is finished with its outcome. Shutdown leaves unfinished research pending, and the query-keyed
upsert makes the retry safe.

## Scenario: Model output is bounded and honest

Link selection allows 2048 output tokens and extraction 4096. These bounded JSON tasks explicitly
disable model reasoning so the response budget remains available for the result, within the existing
three-minute research deadline. Exhaustion is an explicit failed outcome, never an empty
link choice interpreted as success. Runtime diagnostics record finish reason and size, not page or
model content. Invalid or unverifiable extraction returns an error and saves no record. Unknown
contact fields remain null; every retained claim includes its observed source URL and verbatim quote.

## Scenario: Activity survives reopening and behavior recovery

One projection rebuilds the queue neuron and the research activity list from the queue's facts on every
change: every open company as Queued or Running, and the twenty most recent Completed, Failed or Stopped
outcomes with the company and its result or error. Failed and stopped entries explain how to retry. Card keys
are the request ids, so a rebuild never duplicates a card, and reopening or restarting the behaviors shows
the same list. The browser remains mounted below the independently scrolling activity list.

## Scenario: Research needs no storage configuration

With no tables binding, opening shows no database guidance, "result" reads the table without failing, and
research saves to the deployment's default database. A Postgres connection bound to the tables slot before
the table is first defined holds it in the user's own database; a table's placement is pinned then.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "customer-researcher";
    public const string Version = "1.2.3";

    public static readonly AppManifest Manifest = new(
        Title: "Customer Researcher",
        Description: "Turn a company name into source-backed research. Explore official websites, verify the details, and keep the evidence. Runs in the desktop workspace.",
        Operations:
        [
            new("open", "Compose and publish the researcher main layout."),
            new("research", "Queue a company by name, location or website; the queue and status neurons show the outcome."),
            new("stop", "Stop active and queued research without removing saved records."),
            new("result", "Read the stored research row for a company query into {app}/result/{requestId}, or null."),
        ],
        Settings:
        [
            new("Model", "Uses the host default LLM.", ""),
        ],
        Slots:
        [
            new("tables", "postgres"),
        ],
        Icon: "icon.svg");
}
