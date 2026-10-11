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

Invoking "open" opens a "Memory" window. `{app}/apps` lists every installed app with its title, version,
origin and files; each synapse shows its status (Starting, Running, Exited or Stopped), the runs and
failures the brain runtime reports for it, its last run time and, once it has run while Memory follows
runs, its p50. After the calculator's "add" runs three more times, `calculator/add.cs` shows three more
runs. Shared files such as `memory/Memory.cs` show as shared. `{app}/types` lists the installed types by
provider. Opening "docked" skips the window.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "memory";
    public const string Version = "1.0.5";

    public static readonly AppManifest Manifest = new(
        Title: "Memory",
        Description: "Find the synapse that already does what you need: an index of this brain's apps and programs, recalled by meaning and words.",
        Operations:
        [
            new("open", "Show this brain's apps, each synapse's status, runs, failures and p50 run time, and the installed types, then reindex; input \"docked\" skips the window."),
            new("recall", "Rank this brain's synapses and apps against the input by meaning and words, best first; the answer is the text neuron {app}/recalls/{requestId}, and {app}/recall holds the latest."),
            new("reindex", "Rebuild the index of this brain's apps and C# files, with their meaning vectors, from scratch as a new generation; {app}/status reports what was indexed."),
        ],
        Settings: [],
        Slots:
        [
            new("tables", "postgres"),
        ],
        Icon: "icon.svg",
        Toolbar:
        [
            new("open", Label: "Memory"),
            new("recall", Kind: "text", Label: "Recall a synapse"),
        ]);
}
