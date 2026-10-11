using System.Text.Json;
using DigitalBrain.Flutter.Text;

// Shared by the researcher's synapses. The queue is four durable facts, each with exactly one writer, so
// no synapse can overwrite another's update: commands.cs appends requests and records cancellations, worker.cs
// records outcomes and the active marker. The queue neuron is derived from them (projection.cs).
public sealed record ResearchRequest(string Id, string Query);
public sealed record ResearchOutcome(string Id, string Status, string Outcome);
// Stops are the stop requests already applied; Answered is the last one whose status was written.
public sealed record ResearchCancellations(string[] Ids, string[] Stops, string? Answered = null);
public sealed record QueuedResearch(string Id, string Query, string Status, string? Outcome);

public static class ResearchState
{
    static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    // How many applied stop ids the queue remembers. A fresh subscription replays every request the app
    // retains (64), so this must exceed that or a replayed old stop is applied again.
    public const int StopMemory = 128;

    public static IText Requests(DigitalBrainConnection brain, string app) => brain.Get<IText>(app + "/requests");
    public static IText Cancellations(DigitalBrainConnection brain, string app) => brain.Get<IText>(app + "/cancellations");
    public static IText Outcomes(DigitalBrainConnection brain, string app) => brain.Get<IText>(app + "/outcomes");
    public static IText Active(DigitalBrainConnection brain, string app) => brain.Get<IText>(app + "/active-research");
    public static IText Queue(DigitalBrainConnection brain, string app) => brain.Get<IText>(app + "/queue");
    public static IText Status(DigitalBrainConnection brain, string app) => brain.Get<IText>(app + "/status");

    public static Task<ResearchRequest[]> ReadRequests(IText text) => Read(text, Array.Empty<ResearchRequest>());
    public static Task<ResearchOutcome[]> ReadOutcomes(IText text) => Read(text, Array.Empty<ResearchOutcome>());
    public static Task<ResearchCancellations> ReadCancellations(IText text) => Read(text, new ResearchCancellations([], []));
    public static Task Write<T>(IText text, T value) => text.Set(JsonSerializer.Serialize(value, json));

    static async Task<T> Read<T>(IText text, T empty)
    {
        var markdown = (await text.Read()).Markdown;
        return string.IsNullOrWhiteSpace(markdown) ? empty : JsonSerializer.Deserialize<T>(markdown, json) ?? empty;
    }

    // What the status neuron shows for a settled request.
    public static string Display(ResearchOutcome outcome) => outcome.Status == "failed" ? outcome.Outcome : outcome.Outcome.TrimEnd('.');

    // A request is open until it has an outcome; a cancelled one is settled unless it is the running one.
    public static bool IsOpen(ResearchRequest request, HashSet<string> settled, string[] cancelled, string active)
        => !settled.Contains(request.Id) && (request.Id == active || !cancelled.Contains(request.Id));

    // The queue as readers see it: each request with its outcome, running, stopped before it started, or pending.
    public static QueuedResearch[] Derive(ResearchRequest[] requests, ResearchCancellations cancellations, ResearchOutcome[] outcomes, string active)
        => [.. requests.Select(request =>
            outcomes.LastOrDefault(item => item.Id == request.Id) is { } outcome ? new QueuedResearch(request.Id, request.Query, outcome.Status, outcome.Outcome)
            : request.Id == active ? new QueuedResearch(request.Id, request.Query, "running", null)
            : cancellations.Ids.Contains(request.Id) ? new QueuedResearch(request.Id, request.Query, "stopped", "Stopped before research started.")
            : new QueuedResearch(request.Id, request.Query, "pending", null))];
}
