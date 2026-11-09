using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Spi;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace IntoChat.OS.Modules.Flutter.Workspace;

// The debugger's inspector reads, behind the brain membership check: GET /brains/{id}/state?alias= is a unit's current state
// record, GET /brains/{id}/view?alias= its published view {alias, generation, view}. 404 when the alias has nothing saved
// (never installed, or no state or view yet). The journal tail is the framework's GET /brains/{id}/journal/tail.
internal static class UnitReadEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = BrainRoutes.Group(endpoints);
        group.MapGet("/state", (INeuronProxies brain, ISynapseCatalogSource? catalog, string? alias, CancellationToken ct) => State(brain, BrainScope.CurrentId(), catalog, alias, ct));
        group.MapGet("/view", (INeuronProxies brain, ISynapseCatalogSource? catalog, string? alias, CancellationToken ct) => View(brain, BrainScope.CurrentId(), catalog, alias, ct));
    }

    internal static async Task<IResult> State(INeuronProxies brain, string brainId, ISynapseCatalogSource? catalog, string? alias, CancellationToken ct)
    {
        if (!IsAlias(alias)) { return Results.BadRequest(new { error = "Name a state alias." }); }
        if (!Loaded(catalog, alias)) { return Results.NotFound(); }
        var state = await brain.Get<IStateReader>(StateNeuron(brainId, alias)).Read(alias).WaitAsync(ct);
        return state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? Results.NotFound() : Results.Json(state, Json);
    }

    internal static async Task<IResult> View(INeuronProxies brain, string brainId, ISynapseCatalogSource? catalog, string? alias, CancellationToken ct)
    {
        if (!IsAlias(alias)) { return Results.BadRequest(new { error = "Name a state alias." }); }
        if (!Loaded(catalog, alias)) { return Results.NotFound(); }
        var view = await brain.Get<IViewReader>(StateNeuron(brainId, alias)).Read(alias).WaitAsync(ct);
        return view is null ? Results.NotFound() : Results.Json(view, Json);
    }

    // A host that has a synapse catalog answers 404 for an alias no loaded unit declares, without activating a neuron for it.
    private static bool Loaded(ISynapseCatalogSource? catalog, string alias) => catalog is null || catalog.Current.States.ContainsKey(alias);

    // An alias is one path segment of a neuron id; anything else would address another neuron.
    private static bool IsAlias([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? alias) => !string.IsNullOrWhiteSpace(alias) && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static NeuronId StateNeuron(string brainId, string alias) => new(new BrainId(brainId), alias, "default");
}
