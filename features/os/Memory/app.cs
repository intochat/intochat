/*
# Memory

The brain's memory of its own programs. Memory keeps an index of this brain's installed apps: one row
per app (its title, description, spec and operations) and one row per program file (its source, the app
it belongs to and how many times it ran). Each row holds lexical terms, in a table in the app's `tables`
slot (unbound, the brain's own database), and a meaning vector: the row's text (the spec, the file name
and the head of its code) embedded by the brain's embedding model into the vector index
`{brain}/memory/apps`. Recall embeds the question and merges the rows nearest it in meaning with the
rows that hold its words, synapses first, so a person, the Assistant or the Creator finds the synapse
that already does what they need before writing a new one. When the question cannot be embedded, or the
index has no vectors, recall ranks by words alone and says so. Runs nudge the ranking toward the synapse
that ran more. Memory leaves itself out of its index.

"intend" is Memory's first policy: given a message, Memory's own agent rephrases it (it must not answer)
into a brief with a confidence, which is declared on the brain's intents neuron `{brain}/memory/intents`
and rephrased; at or above the `ConfirmGate` setting (default 0.8) the intent is also confirmed. The answer
`{app}/intents/{requestId}` is `ok` or `failed: <reason>` (agent failure, malformed reply or 30 seconds
without one), and a failure declares nothing.

Indexing is explicit: "reindex" rebuilds every row from scratch from what is installed now, as a new
generation that recall switches to only once every row and vector of it is written, and "open" asks for
one. An app changed later is found after the next reindex; a reindex whose rows would be what the
current generation holds changes nothing. A file is a synapse when it has top-level statements; a file of
only type declarations is shared code. Every request is answered once: a redelivered recall finds its
answer already written and a redelivered open finds itself in the `{app}/requests` ledger.

Opening Memory shows the brain as a tree: each app with its title, version, where it came from and its
files, each synapse with its status, the runs and failures the brain runtime counted since it started,
its last run time and its median (p50) run time over its recent runs, then the types installed in the
brain. Memory keeps each synapse's p50 by following the brain's run facts. The dock's Recall field asks
Memory.

## Scenario: Reindexing after a new synapse finds it

After a synapse `greet.cs` that greets a person is written into an installed app, invoking "reindex"
rebuilds the index: `{app}/status` names the apps, rows and meaning vectors indexed and the generation,
and recalling "greet" lists `<app>/greet.cs` among the synapses. A file removed from its app is gone
after the next reindex.

## Scenario: Vector recall ranks by meaning

With the calculator and word count installed and indexed, invoking "recall" with "count the words in a
sentence" writes the text neuron `{app}/recalls/{requestId}` (and `{app}/recall`, the latest answer),
ranked by meaning and words, whose first synapse is `word-count/count.cs`; "add two numbers" puts
`calculator/add.cs` first. Each ranked row shows its words score and its meaning similarity, and the
answer names the similarity a row needs to count as near. A question that no row holds a word of and no row is near in meaning answers that nothing
matches. When the question cannot be embedded the answer says it was ranked by words only. An empty
index is answered from the installed apps by words, and a reindex is requested.

## Scenario: Recall never reads a half-built index

While a reindex writes a new generation, recall answers from the last complete one: the answer names
the generation it ranked, and its row count is the one `{app}/status` reported when that generation was
built.

## Scenario: The apps view shows real run counts

Invoking "open" composes and publishes Memory's main layout. `{app}/apps` lists every installed app with its title, version,
origin and files; each synapse shows its status (Starting, Running, Exited or Stopped), the runs and
failures the brain runtime reports for it, its last run time and, once it has run while Memory follows
runs, its p50. After the calculator's "add" runs three more times, `calculator/add.cs` shows three more
runs. Shared files such as `memory/Memory.cs` show as shared. `{app}/types` lists the installed types by
provider.

## Scenario: Intend rephrases a message into a brief

Invoking "intend" with "fix the thing from yesterday" has Memory's own agent (prompt `intend.md`, `Model` setting)
rephrase it, without answering it, into a brief with a confidence. The intent is declared on
`{brain}/memory/intents` and rephrased; at or above `ConfirmGate` (default 0.8) it is also confirmed. The answer
`{app}/intents/{requestId}` reads `ok`. A malformed agent reply, an agent failure or 30 seconds without a reply
answers `failed: <reason>` and declares nothing.

## Scenario: Remember replaces the working memory

Invoking "remember" with "the user prefers short answers" replaces the text of `{app}/working`, which the
context neuron `working-memory` then carries into Ino's turns, next to `active-intents`. Input over 4,000
characters is refused: `{app}/status` says so and the working memory is unchanged.

## Scenario: A cue moves a waiting intent forward

A Waiting intent with a signal cue (a signal alias, optionally a top-level field equal to a value) or a time
cue is watched. When the signal arrives or the time passes, the first cue to fire moves the intent to
Dispatched ("cue: <alias or time>"), and only while it is still Waiting. A cue that cannot be watched is
written to `{app}/status` and the intent stays Waiting.

## Scenario: Every completed turn becomes an episode

When an Ino turn completes, Ino asks Memory to "capture" it with the conversation and run id. Memory's capture
agent (prompt `capture.md`, `CaptureModel` setting) writes a third-person note of 2 to 10 bullets and an
importance from 1 to 10 into `{app}/episodes` under `agent#run`; a failure, 30 seconds without a reply or a
malformed reply stores the first 300 characters of each side instead, marked fallback. Either note is stored and
embedded with every credential replaced by "[a credential was supplied]" or "[a possible credential was supplied]".
A run already captured is skipped.

## Scenario: Quiet time consolidates episodes into facts

Every captured episode restarts the reminder `{app}/consolidation`; once no episode arrives for `QuietMinutes`
(default 10), and once at startup, Memory claims up to 20 unconsolidated episodes for five minutes and its consolidate
agent (prompt `consolidate.md`, `ConsolidateModel` setting, 60 seconds) returns a verdict. Durable items are asserted on
the facts neuron `{brain}/memory/facts` (idempotent per content-hash dedup key: a hash of the item's normalised kind,
subject, text and episode ids; a contradicted fact is invalidated as superseded), working items replace `{app}/working`, intent items are declared. Health, finances,
third parties, location and routines, and items below `ReviewGate` (default 0.75), go to the review table and are
listed by "review" on `{app}/review`; credentials are dropped. Over 512 current facts the least important is
archived and forgotten. Episodes are marked consolidated last; a failed batch is retried once its claims expire, and
an episode three batches failed on is parked (`claim` reads `parked:<reason>` and `{app}/status` says so) so the
episodes behind it proceed. Every current fact, whoever asserted it, is in the life index as `fact:<id>` until it is
forgotten or invalidated.

## Scenario: A turn about a short preference is captured and consolidated into a profile fact

After the user tells Ino "I like short answers", the turn's episode appears in `{app}/episodes` under `ino#<runId>`
with a short third-person summary and importance 6, and is indexed into the life vector index. Once the quiet period
passes the consolidation asserts the preference as a current Profile fact on the facts neuron, and the episode is
marked consolidated.

## Scenario: A health fact goes to review instead of the facts

After the user says "I have a peanut allergy, keep that in mind", consolidation holds the item back although its
confidence is 0.95: the review table gets one pending row whose reason starts with "sensitive: health" and names the
episode, and the current facts hold nothing about peanuts. A key-shaped secret (a prefixed token, PEM block, JWT, Bearer value, password=value or
high-entropy string) is discarded with the reason `secret` and never reaches the review table, while a named
credential hint ("token: ...", "api key is ...", "secret is ...") goes to review with the reason "possible credential". Invoking "review" lists the pending row on `{app}/review` and the context entry `review-count` carries
the number waiting; "review-accept" with the row id asserts it once as a fact (superseding the current fact it contradicts; an intent row
is declared instead and a working row replaces `{app}/working`) and marks the row accepted, and
"review-reject" marks it rejected without asserting anything.

## Scenario: Recall-life answers Found with sources and Absent with coverage

With the profile fact "The user prefers short answers" current, invoking "recall-life" with a question about it writes
`{app}/recalls/{requestId}` (and `{app}/recall`) beginning with Found and the fact's text, each source shown as
`source: profile <date> fact:<id>`. A question when no memory stands out from the rest answers that nothing in memory
answers it and names the coverage, the first and last episode dates and the number of facts. A Partial answer adds the
neighbouring episodes of its best episode and the transcript around its run.

## Scenario: Forgetting a fact removes it from the profile, recall and the model's view

After a profile fact is forgotten through `IFacts.Forget` (for example when the user says "forget that I like short
answers" and Ino calls it), `Current` no longer lists the fact, the `profile` context entry no longer renders it, the
next conversation's turn does not carry it and its `fact:<id>` entry leaves the life index, so recall-life never
answers with the fact. `Read` still returns it, marked Hidden, with its text, and asserting its dedup key again returns
the hidden fact rather than a new one. The episodes it was learned from remain and may still be recalled at the
episode tier; forgetting episodes is not part of this slice.

Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using IntoChat.OS.Apps;

static class App
{
    public const string Name = "memory";
    public const string Version = "1.2.0";

    public static readonly AppManifest Manifest = new(
        Title: "Memory",
        Description: "Find the synapse that already does what you need: an index of this brain's apps and programs, recalled by meaning and words.",
        Operations:
        [
            new("open", "Show this brain's apps, each synapse's status, runs, failures and p50 run time, and the installed types, then reindex."),
            new("recall", "Rank this brain's synapses and apps against the input by meaning and words, best first; the answer is the text neuron {app}/recalls/{requestId}, and {app}/recall holds the latest."),
            new("reindex", "Rebuild the index of this brain's apps and C# files, with their meaning vectors, from scratch as a new generation; {app}/status reports what was indexed."),
            new("intend", "Rephrase a message into a brief through Memory's agent and record it as an intent; input is JSON {\"message\",\"requestId\",\"conversation\"}, the answer is the text neuron {app}/intents/{requestId}: ok or failed: <reason>."),
            new("capture", "Turn a finished turn into an episode: a short note and an importance in {app}/episodes; input is JSON {\"agent\",\"runId\"}. Done by Memory after each completed Ino turn."),
            new("review", "List the consolidation's pending review items (sensitive or unsure items never published) as the text neuron {app}/review."),
            new("review-accept", "Accept a review item: assert it as a fact (once) and mark the row accepted; input is the review row id."),
            new("review-reject", "Reject a review item: mark the row rejected; input is the review row id."),
            new("recall-life", "Recall from the user's life (facts and episodes) by meaning, recency, authority and importance; the answer is Found, Partial or Absent with sources, written as the text neuron {app}/recalls/{requestId}, and {app}/recall holds the latest."),
            new("remember", "Replace the working memory {app}/working with the input (at most 4,000 characters; longer input is refused on {app}/status)."),
        ],
        Settings:
        [
            new("Model", "Model catalog id for the rephrasing agent; empty uses the default.", ""),
            new("CaptureModel", "Model catalog id for the agent that writes episode notes; empty uses the default.", ""),
            new("ConfirmGate", "Confidence at or above which a rephrased intent is confirmed.", "0.8"),
            new("QuietMinutes", "Minutes without a new episode after which consolidation runs.", "10"),
            new("ConsolidateModel", "Model catalog id for the agent that consolidates episodes into facts; empty uses the default.", ""),
            new("HalfLifeDays", "Days after which a memory's recency weight halves in recall-life (never below 0.3).", "14"),
            new("ReviewGate", "Confidence below which a consolidated item goes to review instead of being published.", "0.75"),
        ],
        Slots:
        [
            new("tables", "postgres"),
        ],
        Icon: "icon.svg",
        Toolbar:
        [
            new("open", Label: "Memory"),
            new("recall", Kind: "text", Label: "Recall a synapse"),
            new("remember", Kind: "text", Label: "Remember"),
            new("review", Label: "Review memories"),
        ],
        Requires: new(Connectors: ["postgres"]));
}
