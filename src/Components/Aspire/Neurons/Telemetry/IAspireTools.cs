namespace IntoChat.Aspire;

// Aspire's MCP server as the aspire neuron uses it: Call runs reading tools only; Command is the single path to a writing tool.
internal interface IAspireTools
{
    bool Available { get; }

    Task<string> Call(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);

    Task<AspireResourceView[]> AllResources(CancellationToken ct);

    Task Command(string resourceName, string commandName, CancellationToken ct);
}
