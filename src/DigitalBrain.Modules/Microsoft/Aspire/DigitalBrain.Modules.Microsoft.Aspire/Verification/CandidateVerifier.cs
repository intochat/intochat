using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DigitalBrain.Mcp;
using DigitalBrain.Microsoft.Aspire.Cluster;

namespace DigitalBrain.Microsoft.Aspire.Verification;

// The developer's loop as a neuron: the candidate's MCP answers for the same brain, its apps
// activate from the copied state, every expectation answers as written, every span appears.
internal sealed class CandidateVerifier(IGrainFactory grains, IClusterHost cluster) : ICandidateVerifier
{
    private const string AppContract = "DigitalBrain.Apps.IApp";

    public async Task<VerificationReport> Verify(string brainId, string sha, CandidateEndpoints endpoints, VerificationPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(plan);
        var clock = Stopwatch.StartNew();
        var server = grains.GetGrain<IMcpServer>($"{brainId}/mcp/candidate-{sha}");
        List<VerificationCheck> checks = [];
        try
        {
            await server.Connect(new($"candidate {sha}", $"{endpoints.Mcp}/brains/{brainId}/mcp"), ct);
            foreach (var check in Checks(brainId, sha, plan, server))
            {
                VerificationCheck outcome;
                try { outcome = await check(ct); }
                catch (Exception error) when (error is InvalidOperationException or HttpRequestException or JsonException)
                { outcome = new(checks.Count.ToString(CultureInfo.InvariantCulture), false, error.Message); }
                checks.Add(outcome);
                if (!outcome.Passed) { break; }
            }
        }
        catch (InvalidOperationException error)
        {
            checks.Add(new("connect", false, error.Message));
        }
        finally
        {
            await server.Disconnect(CancellationToken.None);
        }
        return new([.. checks], clock.Elapsed);
    }

    private IEnumerable<Func<CancellationToken, Task<VerificationCheck>>> Checks(string brainId, string sha, VerificationPlan plan, IMcpServer server)
    {
        yield return async ct =>
        {
            var context = await server.Run("neurons_context", "{}", ct);
            if (context.IsError) { return new("context", false, context.Text); }
            using var document = JsonDocument.Parse(context.Text);
            var answered = document.RootElement.GetProperty("brainId").GetString();
            return new("context", answered == brainId, $"candidate answers for brain '{answered}'");
        };
        yield return async ct =>
        {
            var apps = await server.Run("neurons_list", JsonSerializer.Serialize(new { contract = AppContract }), ct);
            return new("activation", !apps.IsError, apps.IsError ? apps.Text : "the brain's apps list from the copied state");
        };
        foreach (var call in plan.Calls)
        {
            yield return async ct =>
            {
                using var arguments = JsonDocument.Parse(call.Arguments);
                var body = JsonSerializer.Serialize(new { contract = call.Contract, key = call.Key, method = call.Method, arguments = arguments.RootElement });
                var answer = await server.Run("neurons_call", body, ct);
                var passed = !answer.IsError && (call.Expected == "*" || answer.Text.Contains(call.Expected, StringComparison.Ordinal));
                return new($"call {call.Contract} {call.Key} {call.Method}", passed, passed ? answer.Text : $"expected {call.Expected}; answered {answer.Text}");
            };
        }
        foreach (var span in plan.Spans)
        {
            yield return async ct =>
            {
                var count = await cluster.Spans($"candidate-{sha}", span, ct);
                return new($"span {span}", count > 0, $"{count} span(s)");
            };
        }
    }
}
