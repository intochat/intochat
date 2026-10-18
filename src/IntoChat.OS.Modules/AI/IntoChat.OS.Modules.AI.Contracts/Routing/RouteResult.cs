using System.Globalization;
using System.Text;

namespace IntoChat.OS.AI.Routing;

// The chosen contract (full type name) and method, with the router's confidence in that choice.
// An app route names the app and operation instead of a contract fill path; Dispatch then posts
// IApp.Request and no language model runs, when the operation's input needs no filling ("open", a
// toolbar button's "").
[GenerateSerializer, Alias("ai.route-result")]
public sealed record RouteResult(
    [property: Id(0)] string Contract,
    [property: Id(1)] string Method,
    [property: Id(2)] double Confidence,
    [property: Id(3)] Dictionary<string, double> Probabilities,
    [property: Id(4)] string? Key = null,
    [property: Id(5)] string? Tool = null,
    [property: Id(6)] string? App = null,
    [property: Id(7)] string? Operation = null)
{
    private const int MaxAlternatives = 3;

    // Directly executable without the language model: an app route, or a pure read.
    public bool IsDirect => App is not null || Method is "Read" or "List";

    // The chosen route, then the next most likely catalog ids. Ids are Orleans aliases (what the
    // catalog asked Jev over), so a reader maps them back through the catalog, not by name.
    public string Hint()
    {
        var builder = new StringBuilder("Likely capabilities (Jev):\n");
        builder.Append("- ").Append(Contract).Append('.').Append(Method).Append(" (").Append(Confidence.ToString("0.00", CultureInfo.InvariantCulture)).Append(')');
        var chosen = Probabilities.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
        foreach (var (id, probability) in Probabilities.OrderByDescending(pair => pair.Value).Take(MaxAlternatives).Where(pair => pair.Key != chosen))
        { builder.Append("\n- ").Append(id).Append(" (").Append(probability.ToString("0.00", CultureInfo.InvariantCulture)).Append(')'); }
        return builder.ToString();
    }
}

// The route, whether it was dispatched, and the call it made: the neuron key, the JSON arguments,
// and the JSON result (or the refusal reason).
[GenerateSerializer, Alias("ai.route-outcome")]
public sealed record RouteOutcome(
    [property: Id(0)] RouteResult Route,
    [property: Id(1)] bool Dispatched,
    [property: Id(2)] string? Key = null,
    [property: Id(3)] string? Arguments = null,
    [property: Id(4)] string? Result = null,
    [property: Id(5)] string? Error = null);
