namespace DigitalBrain.Runtime.Http.Surface;

/// <summary>
/// Canonical tool names and descriptions for the neuron surface, shared across agent and MCP hosts.
/// </summary>
public static class NeuronSurfaceTools
{
    public const string ListName = "neurons_list";
    public const string SearchName = "neurons_search";
    public const string GetName = "neurons_get";
    public const string CallName = "neurons_call";
    public const string ContextName = "neurons_context";

    public const string ListDescription = "List the neurons that exist in your brain: each one's contract (a full type name for neurons_get and neurons_call), key, creation time and creator. Pass contract to list one type only. To find contracts by what they do, use neurons_search.";
    public const string SearchDescription = "Search neuron contracts by what they do. Returns each match's contract type name, methods, and makes its typed tools available.";
    public const string GetDescription = "Resolve a neuron. contract is a full type name from neurons_list; key is the grain key: an app is {brainScope}/apps/{name}, a connection is {brain}/connection/{connector}/{id}.";
    public const string CallDescription = "Call a neuron method. arguments is a JSON object of named arguments (or, for old clients, an array), omitting CancellationToken. To use an app: IApp.Request with {\"operation\":...,\"input\":...}; then read the neuron the app's spec names for the result.";
    public const string ContextDescription = "Read who you are acting as, the current brain id and scope for forming neuron keys, and the brains you are a member of.";
}
