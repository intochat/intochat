using Microsoft.Agents.AI.Workflows;

namespace IntoChat.OS.Modules.AI.Workflows;

// A workflow a host offers by name; Build makes a fresh instance for each run. A hosting registration, not a wire contract: it carries
// an Agent Framework factory, so it stays out of the AI contracts the sandbox and other clients load.
public sealed record WorkflowDefinition(string Name, string Description, Func<Workflow> Build);
