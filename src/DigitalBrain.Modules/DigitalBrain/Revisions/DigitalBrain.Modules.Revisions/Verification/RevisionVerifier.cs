using System.Diagnostics;
using System.Text.Json;

namespace DigitalBrain.Revisions;

// The developer's loop as a neuron: the child's MCP answers for the same brain, the named
// resources are Running and Healthy, every expectation answers as written, every span appears.
internal sealed class RevisionVerifier(IHttpClientFactory http, ILauncherClient launcher) : IRevisionVerifier
{
    private const string AspireContract = "DigitalBrain.Microsoft.Aspire.IAspire";

    public async Task<VerificationReport> Verify(string brainId, string mcpEndpoint, VerificationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var clock = Stopwatch.StartNew();
        var client = new McpNeuronClient(http.CreateClient("revisions.child"), mcpEndpoint, brainId);
        List<VerificationCheck> checks = [];
        foreach (var check in Checks(brainId, mcpEndpoint, plan, client))
        {
            VerificationCheck outcome;
            try { outcome = await check(ct); }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or JsonException)
            { outcome = new(checks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), false, error.Message); }
            checks.Add(outcome);
            if (!outcome.Passed) { break; }
        }
        return new([.. checks], clock.Elapsed);
    }

    private IEnumerable<Func<CancellationToken, Task<VerificationCheck>>> Checks(string brainId, string mcpEndpoint, VerificationPlan plan, McpNeuronClient client)
    {
        yield return async ct =>
        {
            var context = await client.Call("neurons_context", new { }, ct);
            var answered = context.GetProperty("brainId").GetString();
            return new("context", answered == brainId, $"child answers for brain '{answered}'");
        };
        foreach (var resource in plan.HealthyResources)
        {
            yield return async ct =>
            {
                var aspire = await client.Call("neurons_get", new { contract = AspireContract, key = plan.AspireKey }, ct);
                if (!aspire.GetProperty("Resources").TryGetProperty(resource, out var known)) { return new($"resource {resource}", false, "not reported by the child's AppHost"); }
                var state = known.GetProperty("State").GetString();
                var health = known.TryGetProperty("Health", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
                return new($"resource {resource}", state == "Running" && health == "Healthy", $"{state}/{health ?? "unknown"}");
            };
        }
        foreach (var call in plan.Calls)
        {
            yield return async ct =>
            {
                var answer = await client.Call("neurons_call", new { contract = call.Contract, key = call.Key, method = call.Method, arguments = call.Arguments }, ct);
                var text = answer.ValueKind == JsonValueKind.Null ? "null" : answer.GetRawText();
                var passed = call.Expected == "*" || text.Contains(call.Expected, StringComparison.Ordinal);
                return new($"call {call.Contract} {call.Key} {call.Method}", passed, passed ? text : $"expected {call.Expected}; answered {text}");
            };
        }
        foreach (var span in plan.Spans)
        {
            yield return async ct =>
            {
                var count = await launcher.Spans(mcpEndpoint, span, ct);
                return new($"span {span}", count > 0, $"{count} span(s)");
            };
        }
    }
}