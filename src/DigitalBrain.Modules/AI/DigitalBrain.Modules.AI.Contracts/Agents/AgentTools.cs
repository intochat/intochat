using Microsoft.Extensions.AI;

namespace DigitalBrain.AI.Agents;

// A module contributes agent tools by registering a factory; the context names the trusted scope,
// run and tool call the tools act for.
public sealed record AgentToolContext(string ScopeId, string RunId, string CallId)
{
    public string? DatabaseSource { get; init; }
}

public interface IAgentToolFactory
{
    IReadOnlyList<AIFunction> Create(Func<AgentToolContext> context);
}

public interface IAgentToolSource
{
    string? SourceId => null;
    Task<IAgentToolSession> OpenAsync(IReadOnlyList<string> selectedToolNames,
        Func<AgentToolContext> context, CancellationToken ct);
    Task<IAgentToolSession> OpenBoundAsync(string resource, IReadOnlyList<string> selectedToolNames,
        Func<AgentToolContext> context, CancellationToken ct) => OpenAsync(selectedToolNames, context, ct);
}

// Owns live tool connections until the execution completes.
public interface IAgentToolSession : IAsyncDisposable
{
    IReadOnlyList<AIFunction> Tools { get; }
}

// A tool result that also offers more registered tools to the rest of the turn; the model sees Result.
public sealed record AgentToolOffer(IReadOnlyList<string> Tools, object? Result, AgentToolBinding? Binding = null, bool ReplaceSelection = false);
public sealed record AgentToolBinding(string Source, string Resource);
