using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DigitalBrain.AI.Agents;
using DigitalBrain.Framework;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Apps;

internal sealed class AppAgentTools(IDigitalBrain brain) : IAgentToolSource
{
    public string SourceId => "apps";

    public async Task<IAgentToolSession> OpenBoundAsync(string resource, IReadOnlyList<string> selectedToolNames, Func<AgentToolContext> context, CancellationToken ct)
    {
        var scope = context().ScopeId;
        if (scope != DigitalBrain.Framework.BrainScope.CurrentId())
        { throw new UnauthorizedAccessException("An app tool must use the caller's current brain."); }
        var app = await brain.Get<IApp>(IApp.Key(scope, resource)).Read().WaitAsync(ct);
        if (app.Status != AppStatus.Live)
        { throw new UnauthorizedAccessException("The selected app is not live."); }
        return Create(selectedToolNames, context, scope, [app]);
    }

    public async Task<IAgentToolSession> OpenAsync(IReadOnlyList<string> selectedToolNames, Func<AgentToolContext> context, CancellationToken ct)
    {
        var selected = selectedToolNames.Where(name => name.StartsWith("app_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) { return new Session([]); }
        var scope = context().ScopeId;
        var names = await brain.Get<IAppIndex>(scope).List().WaitAsync(ct);
        var apps = new List<AppSnapshot>();
        foreach (var name in names)
        {
            var app = await brain.Get<IApp>(IApp.Key(scope, name)).Read().WaitAsync(ct);
            if (app.Status == AppStatus.Live) { apps.Add(app); }
        }
        return Create(selectedToolNames, context, scope, apps);
    }

    private IAgentToolSession Create(IReadOnlyList<string> selectedToolNames, Func<AgentToolContext> context, string scope, IReadOnlyList<AppSnapshot> apps)
    {
        var selected = selectedToolNames.ToHashSet(StringComparer.Ordinal);
        var tools = new List<AIFunction>();
        foreach (var app in apps)
        {
            foreach (var operation in app.Manifest.Operations)
            {
                var name = AppToolName.For(app.Name, operation.Name);
                if (!selected.Contains(name)) { continue; }
                async Task<object> Invoke([Description("Input for this app operation, as text or JSON.")] string input, CancellationToken cancellationToken)
                {
                    var caller = context();
                    if (caller.ScopeId != scope || string.IsNullOrEmpty(caller.CallId))
                    { throw new InvalidOperationException("An app tool requires an active call in its brain."); }
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}/{caller.RunId}/{caller.CallId}/{name}"));
                    var invocationId = new Guid(hash.AsSpan(0, 16));
                    var neuron = brain.Get<IApp>(IApp.Key(scope, app.Name));
                    var current = await neuron.Read().WaitAsync(cancellationToken);
                    if (current.Status != AppStatus.Live)
                    { throw new UnauthorizedAccessException("The selected app changed. Discover and select it again."); }
                    var invocation = await neuron.Invoke(new(invocationId, operation.Name, input)).WaitAsync(cancellationToken);
                    var started = Stopwatch.GetTimestamp();
                    while (invocation.Status == InvocationStatus.Pending)
                    {
                        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                        { return new { isError = true, code = "app_timeout", message = "App invocation timed out.", error = "App invocation timed out.", id = invocation.Id }; }
                        await Task.Delay(100, cancellationToken);
                        invocation = await neuron.ReadInvocation(invocation.Id).WaitAsync(cancellationToken);
                    }
                    return invocation.Status == InvocationStatus.Failed
                        ? new { isError = true, code = "app_failed", message = invocation.Error ?? "The app invocation failed.", error = invocation.Error, id = invocation.Id }
                        : invocation;
                }
                tools.Add(AIFunctionFactory.Create(Invoke, name, $"{app.Manifest.Title} ({app.Name}): {operation.Description}"));
            }
        }
        return new Session(tools);
    }

    private sealed class Session(IReadOnlyList<AIFunction> tools) : IAgentToolSession
    {
        public IReadOnlyList<AIFunction> Tools { get; } = tools;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
